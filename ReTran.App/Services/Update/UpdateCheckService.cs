using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Update;

/// <summary>
/// Outcome of a single update check — the UI picks how to react: a toast when
/// <see cref="UpdateAvailable"/> (or <see cref="UpdateCheckService.UpdateFound"/> on the first discovery),
/// a toast when <see cref="AlreadyLatest"/>, an error toast with a manual Check button when
/// <see cref="Failed"/>; the background check stays silent.
/// Kết quả một lần kiểm tra update — UI chọn cách phản hồi: toast khi
/// <see cref="UpdateAvailable"/> (hoặc <see cref="UpdateCheckService.UpdateFound"/> ở lần phát hiện đầu),
/// toast khi <see cref="AlreadyLatest"/>, toast lỗi khi <see cref="Failed"/> với nút Check thủ công;
/// check nền vẫn im lặng.
/// </summary>
public enum UpdateCheckResult
{
    /// <summary>GitHub reported a release newer than the current version. GitHub trả về bản mới hơn phiên bản hiện tại.</summary>
    UpdateAvailable,

    /// <summary>Already on the latest version — the badge state (if any) is cleared. Đã là bản mới nhất — trạng thái badge (nếu có) bị xoá.</summary>
    AlreadyLatest,

    /// <summary>Check could not complete (network loss, GitHub error) — the current state is kept. Không kiểm tra được (mất mạng/lỗi GitHub) — giữ nguyên trạng thái.</summary>
    Failed,
}

/// <summary>
/// Background update check: waits 5s on the first run, then repeats every 24 hours — end users never
/// press a check button themselves, so it has to run in the background. The UI reads the badge through
/// <see cref="IsUpdateAvailable"/> and subscribes to <see cref="UpdateFound"/> to toast exactly once per
/// transition (not again every 24h).
/// Kiểm tra update nền: trễ 5s lần đầu rồi lặp mỗi 24 giờ — end-user không tự bấm check nên phải chạy nền.
/// UI đọc badge qua <see cref="IsUpdateAvailable"/> và đăng ký <see cref="UpdateFound"/> để toast đúng 1 lần
/// mỗi chuyển tiếp (không lặp mỗi 24h).
/// </summary>
public sealed class UpdateCheckService : IAsyncDisposable
{
    private readonly IAppUpdateService _updates;
    private readonly ILogger<UpdateCheckService> _log;
    private readonly bool _isSupported;
    private readonly TimeSpan _initialDelay;

    // Thread-safety: the background loop writes state while the UI thread reads the properties —
    // transitions happen under _sync, events fire OUTSIDE the lock so a re-entrant handler never
    // holds it.
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ReleaseInfo? _availableRelease;

    // Guard against overlapping checks: the 24h loop and the Settings "Check for updates" button can
    // run at the same time — each extra call is a wasted HTTP request. Try-acquire without waiting: if a
    // check is already running the caller gets the current state immediately.
    private readonly SemaphoreSlim _checkGate = new(1, 1);

    /// <summary>
    /// Creates the service — the background loop only starts when <see cref="Start"/> is called.
    /// Tạo service — vòng lặp nền chỉ khởi động khi gọi <see cref="Start"/>.
    /// </summary>
    /// <param name="updates">The update check/download service — returns <see langword="null"/> on error or off Windows. Dịch vụ kiểm tra/tải update — tự trả <see langword="null"/> khi lỗi hoặc không phải Windows.</param>
    /// <param name="log">Logs Information when a new release is found, Warning when a check fails. Ghi Information khi phát hiện bản mới, Warning khi kiểm tra thất bại.</param>
    public UpdateCheckService(IAppUpdateService updates, ILogger<UpdateCheckService> log)
        : this(updates, log, UpdateFeature.IsEnabled, initialDelay: null)
    {
    }

    // Internal overload for tests: the Start() guard cannot be verified against a real non-Windows or
    // DEBUG host from a Release test run — pass isSupported=false to test the no-op, and
    // initialDelay=Zero so that a broken guard makes the loop check immediately while the test waits,
    // instead of forcing a real 5s wait before any behaviour shows up.
    internal UpdateCheckService(
        IAppUpdateService updates,
        ILogger<UpdateCheckService> log,
        bool isSupported,
        TimeSpan? initialDelay = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(log);

        _updates = updates;
        _log = log;
        _isSupported = isSupported;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>The newest discovered release — <see langword="null"/> when none yet, or after it was verified as the latest. Release mới nhất được phát hiện — <see langword="null"/> khi chưa có hoặc đã xác minh là bản mới nhất.</summary>
    public ReleaseInfo? AvailableRelease
    {
        get
        {
            lock (_sync)
            {
                return _availableRelease;
            }
        }
    }

    /// <summary>Whether an update is available — the UI shows a badge. Có bản update khả dụng không — UI hiển thị badge.</summary>
    public bool IsUpdateAvailable
    {
        get
        {
            lock (_sync)
            {
                return _availableRelease is not null;
            }
        }
    }

    /// <summary>Raised when the badge state changes — the UI re-renders. Bắn khi trạng thái badge thay đổi — UI re-render.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Raised exactly once per unavailable→available transition (not repeated when 24h later the same
    /// release is reported again) — so the UI toast shows only one time.
    /// Bắn đúng một lần cho mỗi chuyển tiếp không→có (không lặp lại khi 24h sau vẫn báo cùng bản) —
    /// UI toast chỉ hiện một lần.
    /// </summary>
    public event Action<ReleaseInfo>? UpdateFound;

    /// <summary>
    /// Runs in the background: waits 5s, then checks and repeats every 24 hours — a no-op when
    /// <see cref="UpdateFeature.IsEnabled"/> is <see langword="false"/> (DEBUG build or non-Windows);
    /// calling it again is a no-op.
    /// Chạy nền: trễ 5s rồi kiểm tra và lặp mỗi 24 giờ — noop khi tính năng bị tắt; gọi lần 2 chỉ là no-op.
    /// </summary>
    public void Start()
    {
        // The updater is a Windows-only process and must never run against a DEBUG build — mobile/mac
        // have nothing to check, so Start is a no-op there.
        if (!_isSupported || _loop is not null) return;

        // Capture the CTS locally: DisposeAsync may set _cts = null before the Task gets a chance to run
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    /// <summary>
    /// Runs a single check (the Settings "Check for updates" button calls it directly) — on failure it
    /// returns <see cref="UpdateCheckResult.Failed"/> and KEEPS the current state: one failed check must
    /// not clear the badge of a previously discovered update. When a check is already running (the 24h
    /// loop overlapping a manual one) it returns the current state immediately without a second HTTP call.
    /// Kiểm tra một lần (nút "Kiểm tra cập nhật" trong Settings gọi thẳng) — thất bại trả
    /// <see cref="UpdateCheckResult.Failed"/> và GIỮ nguyên trạng thái: một lần check lỗi không được xoá
    /// badge của bản update đã phát hiện trước đó. Đã có lần check đang chạy (24h loop + manual chồng lấp)
    /// → trả trạng thái hiện tại ngay, không gửi HTTP lần thứ hai.
    /// </summary>
    /// <returns>The result of the check. Kết quả lần kiểm tra.</returns>
    public async Task<UpdateCheckResult> CheckOnceAsync()
    {
        // Try-acquire without waiting: no duplicate HTTP while a check is running — report the known
        // badge state (Available) or Failed (silent, state unchanged)
        if (!_checkGate.Wait(0))
        {
            return AvailableRelease is not null
                ? UpdateCheckResult.UpdateAvailable
                : UpdateCheckResult.Failed;
        }

        try
        {
            return await CheckOnceCoreAsync();
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> CheckOnceCoreAsync()
    {
        ReleaseInfo? release;
        try
        {
            release = await _updates.CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            // The IAppUpdateService contract already returns null on error — catch again for other
            // implementations so no exception escapes the caller (the UI does not toast on a failed check)
            _log.LogWarning("Update check failed: {Message}", ex.Message);
            return UpdateCheckResult.Failed;
        }

        if (release is null)
        {
            _log.LogWarning("Update check failed.");
            return UpdateCheckResult.Failed;
        }

        if (!_updates.IsUpdateAvailable(release))
        {
            ReleaseInfo? previous;
            lock (_sync)
            {
                previous = _availableRelease;
                _availableRelease = null;
            }

            // Verified as the latest version — clear the badge (the user may have installed it already)
            if (previous is not null)
            {
                StateChanged?.Invoke();
            }

            return UpdateCheckResult.AlreadyLatest;
        }

        ReleaseInfo? prior;
        lock (_sync)
        {
            prior = _availableRelease;
            _availableRelease = release;
        }

        var firstDiscovery = prior is null;

        // Compare by TagName instead of record equality: every check deserializes a fresh
        // ReleaseInfo instance (List<ReleaseAsset> compares by reference) — otherwise StateChanged
        // would fire every 24h even though the badge did not change.
        if (prior is null || prior.TagName != release.TagName)
        {
            StateChanged?.Invoke();
        }

        if (firstDiscovery)
        {
            // Information only on the first discovery — avoids repeating the same log line every 24h
            _log.LogInformation("Update available: {TagName}", release.TagName);
            UpdateFound?.Invoke(release);
        }

        return UpdateCheckResult.UpdateAvailable;
    }

    /// <summary>
    /// Background loop: waits <see cref="_initialDelay"/> then checks, afterwards every 24 hours — one
    /// failing cycle must not kill the loop, and nothing ever escapes it.
    /// Vòng lặp nền: trễ <see cref="_initialDelay"/> rồi check, sau đó lặp mỗi 24 giờ — một chu kỳ lỗi
    /// không được giết vòng lặp, không bao giờ ném ra ngoài.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // First-run delay: the app has just opened and is still busy — avoid hitting GitHub at startup
            await Task.Delay(_initialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // the app closed before the first check
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // CheckOnceAsync already swallows network errors → Failed; catch here for system-level
                // failures (an event handler throwing...) so the loop does not die
                await CheckOnceAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One failing cycle must not kill the loop — the next cycle still checks. Logging can
                // throw too (a broken log sink) — swallowing is acceptable here because this is
                // best-effort diagnostics, otherwise the loop would die permanently.
                try
                {
                    _log.LogWarning("Update check loop error: {Message}", ex.Message);
                }
                catch
                {
                    // Already swallowed: nowhere safe left to report the error
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Stops the background loop — waits for an in-flight check so disposal never cuts it mid-way on app exit. Dừng vòng lặp nền — chờ check đang chạy kết thúc để không cắt giữa chừng khi app thoát.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // The in-flight cycle was cancelled on dispose — already handled
            }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}