using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Update;

/// <summary>
/// The kind of failure that stopped the update pipeline — the UI picks the toast message per kind (spec §11).
/// Loại lỗi khiến luồng cập nhật dừng giữa chừng — UI chọn thông điệp toast theo loại (spec §11).
/// </summary>
public enum UpdateError
{
    /// <summary>Download of the zip failed (network loss, bad asset...). Tải zip thất bại (mất mạng, asset sai...).</summary>
    DownloadFailed,

    /// <summary>Signature verification of the downloaded package failed. Xác thực chữ ký của gói đã tải thất bại.</summary>
    VerifyFailed,

    /// <summary>Stopping app services failed or timed out — the update cannot safely install. Dừng service của app thất bại/hết timeout — không thể cài an toàn.</summary>
    StopServicesFailed,

    /// <summary>Launching the updater failed. Khởi chạy updater thất bại.</summary>
    InstallFailed,
}

/// <summary>
/// Quits the app after the updater has been launched — separated so the coordinator can be unit-tested
/// (the MAUI implementation calls <c>Application.Current.Quit()</c>, spec §7 step 5).
/// Thoát ứng dụng sau khi updater đã được khởi chạy — tách interface để test được coordinator
/// (impl MAUI gọi <c>Application.Current.Quit()</c>, spec §7 bước 5).
/// </summary>
public interface IAppShutdown
{
    /// <summary>Quits the current application. Đóng ứng dụng hiện tại.</summary>
    void Quit();
}

/// <summary>
/// Stops the app services that would hold file locks or the overlay port during install
/// (pipeline, core process, overlay feed server — spec §6.2 step 3).
/// Dừng các service của app đang giữ khóa file hoặc cổng overlay trong lúc cài
/// (pipeline, core process, overlay feed server — spec §6.2 bước 3).
/// </summary>
public interface IUpdateStopper
{
    /// <summary>
    /// Attempts every service cleanup in pipeline/core/overlay order, then propagates any step failure.
    /// Thử cleanup mọi service theo thứ tự pipeline/core/overlay, rồi ném bất kỳ lỗi nào của các bước.
    /// </summary>
    /// <param name="cancellationToken">Cancellation (user Dispose / ~30s combined timeout). Token huỷ (Dispose người dùng / timeout tổng hợp ~30s).</param>
    /// <returns>A task that completes when all service cleanup steps have been attempted. Task hoàn tất khi đã thử mọi bước cleanup service.</returns>
    /// <exception cref="TimeoutException">A service stop step exceeded its own timeout. Một bước dừng service vượt timeout riêng của bước đó.</exception>
    /// <exception cref="OperationCanceledException">The supplied token was cancelled. Token được cung cấp đã bị huỷ.</exception>
    /// <exception cref="AggregateException">More than one service stop step failed; individual failures are in <see cref="AggregateException.InnerExceptions"/>. Nhiều bước dừng service cùng lỗi; từng lỗi nằm trong <see cref="AggregateException.InnerExceptions"/>.</exception>
    Task StopServicesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Update pipeline orchestrator (spec §6.2/§7): download zip → verify signature → stop services
/// (~30s combined timeout) → launch updater → quit the app. Every fail branch cancels the run's CTS,
/// fires <see cref="Failed"/> and resets state via the <c>runId</c>-guarded reset so a stale run can
/// never clobber a newer one started from an event handler.
/// Orchestrator luồng cập nhật (spec §6.2/§7): tải zip → xác thực chữ ký → dừng service (timeout tổng
/// hợp ~30s) → khởi chạy updater → thoát app. Mọi nhánh fail đều huỷ CTS của run, bắn
/// <see cref="Failed"/> và reset trạng thái qua reset guard theo <c>runId</c> để run cũ không bao giờ
/// phá trạng thái run mới được khởi động từ handler event.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private readonly IAppUpdateService _updates;
    private readonly IUpdateStopper _stopper;
    private readonly IAppShutdown _shutdown;
    private readonly ILogger<UpdateCoordinator> _log;

    // Thread-safety: the pipeline runs on a background continuation while the UI reads the properties —
    // every state transition happens under _sync; Changed/Cancelled/Failed fire OUTSIDE the lock so a
    // re-entrant handler never holds it (handlers only read state).
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private bool _updateInProgress;
    private bool _isFinalizing;
    private int _downloadPercent;

    // Run sequence number — the reset of an old run only takes effect while _runId still matches;
    // otherwise a trailing release (Fail/Cancel/Dispose running AFTER a handler already started a new
    // run) would reset the state of run #2.
    private int _runId;

    /// <summary>
    /// Creates the coordinator.
    /// Tạo coordinator mới.
    /// </summary>
    /// <param name="updates">Download/verify/install service. Dịch vụ tải/xác thực/cài zip update.</param>
    /// <param name="stopper">Stops app services before install. Dừng các service của app trước khi cài.</param>
    /// <param name="shutdown">Quits the app after the updater is launched. Thoát app sau khi updater được khởi chạy.</param>
    /// <param name="log">Warns when a pipeline branch fails. Ghi Warn khi một nhánh của pipeline fail.</param>
    public UpdateCoordinator(
        IAppUpdateService updates,
        IUpdateStopper stopper,
        IAppShutdown shutdown,
        ILogger<UpdateCoordinator> log)
    {
        _updates = updates;
        _stopper = stopper;
        _shutdown = shutdown;
        _log = log;
    }

    /// <summary>Whether an update is running — the UI disables the "Update now" button. Có update đang chạy không — UI khoá nút "Cập nhật ngay".</summary>
    public bool UpdateInProgress
    {
        get
        {
            lock (_sync)
            {
                return _updateInProgress;
            }
        }
    }

    /// <summary><see langword="true"/> while stopping services / installing — the UI hides the Cancel button (cannot cancel midway). <see langword="true"/> khi đang dừng service/install — UI ẩn nút Hủy (không huỷ được giữa chừng).</summary>
    public bool IsFinalizing
    {
        get
        {
            lock (_sync)
            {
                return _isFinalizing;
            }
        }
    }

    /// <summary>Zip download progress 0..100. Tiến độ tải zip 0..100.</summary>
    public int DownloadPercent
    {
        get
        {
            lock (_sync)
            {
                return _downloadPercent;
            }
        }
    }

    /// <summary>Fires when a property changes — the modal UI re-renders. Bắn khi một property đổi — modal UI re-render.</summary>
    public event Action? Changed;

    /// <summary>Fires when the update failed (state already reset) — the UI toasts the error. Bắn khi update thất bại (trạng thái đã reset) — UI toast lỗi.</summary>
    public event Action<UpdateError>? Failed;

    /// <summary>Fires when the user cancels before finalize — the UI toasts "Update cancelled". Bắn khi người dùng Hủy trước khi finalise — UI toast "Đã hủy cập nhật".</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Runs the update pipeline: download (progress reported through <see cref="DownloadPercent"/>),
    /// verify the signature, stop services, launch the updater, then quit the app. Completes when the
    /// pipeline ends (success, failure or cancel).
    /// Chạy pipeline cập nhật: tải (báo tiến độ qua <see cref="DownloadPercent"/>), xác thực chữ ký,
    /// dừng service, khởi chạy updater rồi thoát app. Hoàn tất khi pipeline kết thúc (thành công,
    /// thất bại hoặc huỷ).
    /// </summary>
    /// <param name="release">The release to download. Release cần tải.</param>
    /// <returns>A task completing when the pipeline ends. Task hoàn tất khi pipeline kết thúc.</returns>
    /// <exception cref="InvalidOperationException">Thrown when an update is already running. Ném khi đã có update đang chạy.</exception>
    public async Task StartUpdateAsync(ReleaseInfo release)
    {
        // The updater only works on the Windows desktop — no such flow on mobile/mac.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CancellationTokenSource cts;
        int runId;
        lock (_sync)
        {
            if (_updateInProgress)
            {
                throw new InvalidOperationException("An update is already in progress.");
            }

            // The previous run's CTS has finished (nobody cancels it anymore) — dispose to drop registrations.
            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            _updateInProgress = true;
            runId = ++_runId;
            _downloadPercent = 0;
        }

        Changed?.Invoke();

        // A seed failure (broken DI...) must not escape — otherwise UpdateInProgress sticks true
        // and no Failed ever reports it to the UI.
        string? zipPath;
        try
        {
            zipPath = await _updates.DownloadUpdateAsync(release, new PercentProgress(this));
        }
        catch (Exception ex)
        {
            // A LATE fault after CancelUpdate/Dispose superseded this run must exit silently —
            // mirrors the success-path check below; Fail's runId guard is the second line of
            // defense if the cancel lands between this check and Fail's lock.
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Fail(UpdateError.DownloadFailed, $"Update download failed: {ex.Message}", runId);
            return;
        }

        // CancelUpdate/Dispose won: the canceller already reset the state (Cancelled fired, flags
        // cleared) — just return. Checked AFTER the await because the download API has no token;
        // the null branch below is therefore never reached for a superseded run.
        if (cts.IsCancellationRequested)
        {
            return;
        }

        if (zipPath is null)
        {
            Fail(UpdateError.DownloadFailed, "Update download failed: no zip file returned.", runId);
            return;
        }

        // Verify BEFORE stopping services — a bad package must not drag the services down with it.
        if (!_updates.VerifyDownloadedPackage())
        {
            Fail(UpdateError.VerifyFailed, "Update aborted: downloaded package verification failed.", runId);
            return;
        }

        // Check + set atomic under _sync: either CancelUpdate sees IsFinalizing (no-op), or the
        // pipeline sees !UpdateInProgress (return) — they can never both proceed.
        lock (_sync)
        {
            if (!_updateInProgress || cts.IsCancellationRequested)
            {
                return;
            }

            _isFinalizing = true;
        }

        Changed?.Invoke();

        // Stop services with a ~30s combined budget (spec §6.2: ~10s per step) — the run's own
        // cancel and the timeout share one linked token; either way the stop cannot hang forever.
        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, stopTimeout.Token);
            await _stopper.StopServicesAsync(stopCts.Token);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            // The RUN's CTS was cancelled (Dispose won during the stop window — CancelUpdate is a
            // no-op while finalizing), not the 30s timeout: the app is tearing down, so abort
            // silently — Warn + reset only, never raise Failed mid-teardown. The run-CTS check
            // takes precedence over the timeout in the filter (when the run is dead the timeout
            // is irrelevant).
            _log.LogWarning("{Message}", $"Update aborted: cancelled while stopping services: {ex.Message}");
            Reset(runId);
            return;
        }
        catch (Exception ex)
        {
            // A service error, per-step timeout, or the combined stop timeout is fail-closed:
            // UpdateStopper propagates it after best-effort cleanup and the updater is never launched.
            Fail(UpdateError.StopServicesFailed, $"Update aborted: stop services failed: {ex.Message}", runId);
            return;
        }

        // Dispose may have cancelled the run while a non-observant stopper completed normally —
        // the same silent abort applies (there is no Failed to raise from here on either).
        if (cts.IsCancellationRequested)
        {
            _log.LogWarning("{Message}", "Update aborted: cancelled while stopping services.");
            Reset(runId);
            return;
        }

        bool installed;
        try
        {
            installed = await _updates.InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            Fail(UpdateError.InstallFailed, $"Update aborted: installer launch failed: {ex.Message}", runId);
            return;
        }

        if (!installed)
        {
            Fail(UpdateError.InstallFailed, "Update aborted: installer launch failed.", runId);
            return;
        }

        try
        {
            _shutdown.Quit();
        }
        catch (Exception ex)
        {
            // Quit failed = the process is still alive: the update genuinely succeeded (the updater
            // was launched) so only Warn + reset — firing Failed would claim the opposite. A successful
            // Quit never resets: the process is supposed to be gone.
            _log.LogWarning("{Message}", $"Application quit failed after update install: {ex.Message}");
            Reset(runId);
        }
    }

    /// <summary>
    /// Cancels the update (the modal's Cancel button) — no-op when nothing is running, when already
    /// finalizing or when already cancelled. Fires <see cref="Cancelled"/> BEFORE the state resets, so
    /// the handler still sees <see cref="UpdateInProgress"/> as <see langword="true"/>.
    /// Hủy update (nút Hủy trong modal) — no-op khi chưa start, đã finalise hoặc đã huỷ trước đó.
    /// Bắn <see cref="Cancelled"/> TRƯỚC khi reset trạng thái — handler thấy <see cref="UpdateInProgress"/>
    /// còn <see langword="true"/>.
    /// </summary>
    public void CancelUpdate()
    {
        int runId;
        lock (_sync)
        {
            // IsCancellationRequested: a second cancel (re-entrant/concurrent handler) is a no-op —
            // avoids firing Cancelled twice; cts.Cancel shares the lock with the guard for a
            // consistent transition.
            var cts = _cts;
            if (!_updateInProgress || _isFinalizing || cts is null || cts.IsCancellationRequested)
            {
                return;
            }

            runId = _runId;
            cts.Cancel();
        }

        Cancelled?.Invoke();
        Reset(runId);
    }

    /// <summary>
    /// Teardown: cancels the CTS and releases a pending update (if any). Does NOT fire
    /// <see cref="Cancelled"/> — this is not a user action. Actual behavior per window:
    /// before finalize (download/verify) the pipeline exits at its next cancellation check and the
    /// state resets silently; during the stop-services step the run aborts silently (Warn + reset,
    /// no <see cref="Failed"/>); during install the pipeline still proceeds to launch the updater
    /// and Quit — the app is tearing down, events may still fire after Dispose.
    /// Teardown: huỷ CTS và giải phóng update đang chờ (nếu có). Không bắn <see cref="Cancelled"/> —
    /// đây không phải hành động người dùng. Hành vi thực tế theo window: trước finalise
    /// (download/verify) pipeline thoát ở check huỷ kế tiếp và reset im lặng; trong bước dừng
    /// service, run hủy im lặng (Warn + reset, KHÔNG <see cref="Failed"/>); trong lúc install,
    /// pipeline vẫn chạy tiếp khởi chạy updater và Quit — app đang teardown; event có thể vẫn
    /// fire sau Dispose.
    /// </summary>
    public void Dispose()
    {
        int runId;
        bool release;
        lock (_sync)
        {
            _cts?.Cancel();
            runId = _runId;
            release = _updateInProgress && !_isFinalizing;
        }

        if (release)
        {
            Reset(runId);
        }
    }

    /// <summary>
    /// Stops the pipeline on an error: cancels the run's CTS, logs Warn, fires <see cref="Failed"/>,
    /// then resets the state. A STALE run (<c>runId</c> no longer matching, i.e. CancelUpdate/Dispose
    /// superseded it and a newer run owns the state) only logs — it never cancels the current run's
    /// CTS, never fires <see cref="Failed"/> and never resets. The <see cref="Failed"/> handler still
    /// sees <see cref="UpdateInProgress"/> as <see langword="true"/> — a re-entrant Start without
    /// <see cref="Dispose"/> throws; if the handler Disposes then Starts a new run, the trailing
    /// release behind it is a no-op thanks to the <c>runId</c> check.
    /// Dừng update vì lỗi: huỷ CTS của run, log Warn, bắn <see cref="Failed"/>, MỚI reset trạng thái.
    /// Run CŨ (<c>runId</c> không còn khớp — CancelUpdate/Dispose đã thay thế nó và run mới đang sở
    /// hữu trạng thái) chỉ log — không huỷ CTS của run hiện tại, không bắn <see cref="Failed"/>,
    /// không reset. Handler của <see cref="Failed"/> thấy <see cref="UpdateInProgress"/> còn
    /// <see langword="true"/> — Start lại không qua <see cref="Dispose"/> sẽ throw; nếu handler
    /// Dispose rồi Start run mới, release trailing phía sau là no-op nhờ check <c>runId</c>.
    /// </summary>
    /// <param name="error">The failure kind for the UI. Loại lỗi cho UI.</param>
    /// <param name="message">Warn log message. Thông điệp ghi Warn.</param>
    /// <param name="runId">The failing pipeline's own run number — never re-read from shared state. Số thứ tự run của chính pipeline đang fail — không đọc lại từ state chung.</param>
    private void Fail(UpdateError error, string message, int runId)
    {
        bool active;
        lock (_sync)
        {
            // active only for THIS run: a stale run must not cancel the newer run's CTS, fire a
            // spurious Failed or reset the newer run's state.
            active = _updateInProgress && _runId == runId;
            if (active)
            {
                // Cancel the CTS BEFORE firing Failed: the pipeline checks IsCancellationRequested
                // after its awaits and must terminate; otherwise a stale run could finalize on top of
                // a new run started from the handler.
                _cts?.Cancel();
            }
        }

        // Warn always runs, even when the run was already stopped by cancel/dispose — every error is logged.
        _log.LogWarning("{Message}", message);
        if (!active)
        {
            return;
        }

        Failed?.Invoke(error);
        Reset(runId);
    }

    /// <summary>
    /// Resets the run's flags under the lock, then fires <see cref="Changed"/> outside it. No-op when
    /// the run has already ended or <paramref name="runId"/> no longer matches — a stale run's release
    /// must not clobber a newer run's state.
    /// Reset cờ của run dưới lock, rồi mới bắn <see cref="Changed"/> ngoài lock. No-op khi run đã kết
    /// thúc hoặc <paramref name="runId"/> không còn khớp — release của run cũ không được phá trạng
    /// thái run mới.
    /// </summary>
    /// <param name="runId">The run sequence number to release — captured by the caller under the same lock as the state. Số thứ tự run muốn release — bắt từ caller dưới cùng lock với state.</param>
    private void Reset(int runId)
    {
        lock (_sync)
        {
            if (!_updateInProgress || _runId != runId)
            {
                return;
            }

            _updateInProgress = false;
            _isFinalizing = false;
        }

        Changed?.Invoke();
    }

    private void SetDownloadPercent(int value)
    {
        var clamped = Math.Clamp(value, 0, 100);
        lock (_sync)
        {
            if (_downloadPercent == clamped)
            {
                return;
            }

            _downloadPercent = clamped;
        }

        Changed?.Invoke();
    }

    // Direct IProgress instead of Progress<T>: Progress<T> posts via the SynchronizationContext of
    // the creating thread — in tests (no context) reports would arrive late or on the wrong thread.
    private sealed class PercentProgress(UpdateCoordinator owner) : IProgress<int>
    {
        public void Report(int value) => owner.SetDownloadPercent(value);
    }
}
