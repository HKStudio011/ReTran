using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using ReTran_App.Core;
using ReTran_App.Services;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Tray;
using SD = System.Drawing;
using SDD = System.Drawing.Drawing2D;
using SDI = System.Drawing.Imaging;
using SDT = System.Drawing.Text;

namespace ReTran_App.Services.FloatingMenu;

/// <summary>
/// Wires the floating menu's intents and window events to settings, the overlay toggle and the
/// pipeline: every intent persists its change immediately (applied on the next start), rebuilds the
/// MenuState snapshot with live RunStats numbers, renders it through <see cref="FloatingMenuRenderer"/>
/// and pushes the bitmap to the host. Redraws are leading-edge throttled (default 66 ms ≈ 15 fps)
/// and only happen on real state change — no timer, no new thread.
/// Nối các intent và sự kiện cửa sổ của menu nổi với cài đặt, công tắc lớp phủ và pipeline: mỗi
/// intent lưu thay đổi ngay (áp dụng ở lần Start kế tiếp), dựng lại snapshot MenuState kèm số liệu
/// RunStats trực tiếp, render qua FloatingMenuRenderer và đẩy bitmap cho host. Redraw được throttle
/// leading-edge (mặc định 66 ms ≈ 15 fps) và chỉ xảy ra khi state đổi thật — không timer, không thread mới.
/// </summary>
/// <remarks>
/// Threading: the service is not bound to a UI thread. Host events arrive on the overlay worker
/// thread; pipeline events on the peer read-loop thread; stats events on RunStats' timer or frame
/// threads; settings.Changed on whichever thread saved. Every state mutation and draw is serialized
/// by one lock (re-entrant per thread, so a save's Changed handler re-entering RequestRedraw is
/// safe); under it the service only does bounded work — small JSON file I/O, one GDI+ panel render,
/// non-blocking host enqueues, one notifier call. Async intents await RPC OUTSIDE the lock and
/// commit their result under it. Never take another service's gate while holding ours: the overlay
/// service's IsEnabled getter and SetEnabled both take theirs, and its save fires settings.Changed
/// synchronously back into our handler — so read what is needed under our gate and act on the outside.
/// Rendered bitmaps are cached per window size and handed to the host,
/// which consumes them asynchronously on its worker thread — they are therefore kept alive (and
/// reused) until <see cref="Dispose"/>, never disposed per draw.
/// Threading: service không gắn với UI thread. Event host đến trên worker thread của overlay; event
/// pipeline trên thread read-loop peer; event stats trên timer hoặc thread khung hình của RunStats;
/// settings.Changed trên thread nào lưu file. Mọi thay đổi state và mọi lần vẽ được serialize bởi
/// một lock (re-entrant theo thread, nên handler Changed của một lần save gọi lại RequestRedraw vẫn
/// an toàn); dưới lock service chỉ làm việc có giới hạn — I/O JSON nhỏ, render GDI+ một panel,
/// enqueue host không chặn, một lần gọi notifier. Intent async await RPC NGOÀI lock rồi commit kết
/// quả DƯỚI lock. Không bao giờ lấy gate của service khác khi đang giữ gate của mình: cả getter
/// IsEnabled lẫn SetEnabled của service overlay đều lấy gate riêng của chúng, và lần lưu của nó phát
/// settings.Changed đồng bộ trở lại handler của ta — nên đọc những gì cần dưới gate của mình rồi
/// hành động ở bên ngoài. Bitmap render được cache theo cỡ window và đưa cho host — host tiêu thụ
/// chúng bất đồng bộ trên worker thread — nên chúng được giữ sống (và tái sử dụng) đến Dispose,
/// không dispose sau mỗi lần vẽ.
/// </remarks>
public sealed class FloatingMenuService : IDisposable
{
    /// <summary>Default redraw throttle: 66 ms (≈15 fps), same as OverlayWindowService.DefaultRedrawInterval. Throttle redraw mặc định: 66 ms (≈15 fps), như DefaultRedrawInterval của OverlayWindowService.</summary>
    private static readonly TimeSpan DefaultRedrawInterval = TimeSpan.FromMilliseconds(66);

    /// <summary>Cycle list for the source language — mirrors SettingsPanel.razor's SrcCodes, pinned here to avoid parsing Razor. Danh sách cycle ngôn ngữ nguồn — gương SrcCodes trong SettingsPanel.razor, ghim ở đây để tránh parse Razor.</summary>
    private static readonly string[] SrcCodes = ["auto", "en", "ja", "ko", "zh", "ru", "fr", "de", "es"];

    /// <summary>Cycle list for the destination language — mirrors SettingsPanel.razor's DstCodes, pinned here to avoid parsing Razor. Danh sách cycle ngôn ngữ đích — gương DstCodes trong SettingsPanel.razor, ghim ở đây để tránh parse Razor.</summary>
    private static readonly string[] DstCodes = ["vi", "en", "ja", "ko", "zh", "ru", "fr", "de", "es"];

    // Real monitor work areas (rcWork), queried once at Start() — ReTran.App has no Forms reference
    // and Win32AnchorTracker's enumeration is private (and returns rcMonitor, not rcWork). Until the
    // query runs — and whenever it fails — the layout fallback stands in.
    // Các work area thật của các monitor (rcWork), truy vấn một lần tại Start() — ReTran.App không tham
    // chiếu Forms và phép liệt kê của Win32AnchorTracker là private (lại trả rcMonitor, không phải
    // rcWork). Trước khi truy vấn chạy — và mỗi khi nó lỗi — vùng dự phòng của layout đứng vào.
    private (int Left, int Top, int Right, int Bottom)[] _workAreas = [FloatingMenuLayout.FallbackWorkArea];

    private readonly AppSettingsService _settings;
    private readonly IOverlayToggle _overlay;
    private readonly IPipelineControl _pipeline;
    private readonly IMenuWindowHost _host;
    private readonly AppNotifier _notifier;
    private readonly RunStats _stats;
    private readonly I18nService _L;
    private readonly ILogger<FloatingMenuService> _log;
    private readonly TimeSpan _redrawInterval;
    private readonly object _gate = new();

    private int _posX, _posY;
    private int _windowW, _windowH;
    private DateTime _lastDrawUtc = DateTime.MinValue;
    private bool _visible;
    private bool _statsOpen;
    private bool _sourceListOpen;
    private string? _errorLine;
    private IReadOnlyList<SourceItem> _sources = Array.Empty<SourceItem>();
    private readonly Dictionary<(int W, int H), SD.Bitmap> _surfaces = new();
    private bool _disposed;

    /// <summary>
    /// Creates the service and subscribes to the pipeline, settings and stats change events.
    /// Tạo service và đăng ký event thay đổi của pipeline, cài đặt và thống kê.
    /// </summary>
    /// <param name="settings">Settings store — every intent persists through it immediately. Nơi lưu cài đặt — mỗi intent lưu qua nó ngay.</param>
    /// <param name="overlay">Overlay toggle surface (the local game overlay window). Bề mặt bật/tắt lớp phủ (cửa sổ overlay game local).</param>
    /// <param name="pipeline">Pipeline control surface. Bề mặt điều khiển pipeline.</param>
    /// <param name="host">Menu window host (layered, non-activating). Host cửa sổ menu (layered, không kích hoạt).</param>
    /// <param name="notifier">Shared error-notification route (in-app toast or tray balloon). Đường thông báo lỗi dùng chung (toast trong app hoặc balloon tray).</param>
    /// <param name="stats">Shared run statistics feeding the stats block. Thống kê chạy dùng chung cho vùng thống kê.</param>
    /// <param name="L">Localized string table. Bảng chuỗi đã dịch.</param>
    /// <param name="log">Logger. Bộ ghi log.</param>
    /// <param name="redrawInterval">Minimum gap between two draws (leading edge); null selects 66 ms. Khoảng tối thiểu giữa hai lần vẽ (leading-edge); null chọn 66 ms.</param>
    public FloatingMenuService(AppSettingsService settings, IOverlayToggle overlay, IPipelineControl pipeline,
        IMenuWindowHost host, AppNotifier notifier, RunStats stats, I18nService L,
        ILogger<FloatingMenuService> log, TimeSpan? redrawInterval = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(L);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _overlay = overlay;
        _pipeline = pipeline;
        _host = host;
        _notifier = notifier;
        _stats = stats;
        _L = L;
        _log = log;
        _redrawInterval = redrawInterval ?? DefaultRedrawInterval;

        _pipeline.StateChanged += OnRunStateChanged;
        _pipeline.PipelineStateChanged += OnPipelineLoopChanged;
        _settings.Changed += OnSettingsChanged;
        _stats.Changed += OnStatsChanged;
    }

    /// <summary>Whether the menu window is currently shown (not merely created). Cửa sổ menu đang được hiện hay không (chưa phải chỉ đã tạo).</summary>
    public bool IsVisible { get { lock (_gate) return _visible; } }

    /// <summary>
    /// The MenuState snapshot of the most recent draw, or null before the first one (test seam).
    /// Snapshot MenuState của lần vẽ gần nhất, hoặc null trước lần đầu (điểm cho test).
    /// </summary>
    internal MenuState? LastDrawnState { get; private set; }

    /// <summary>
    /// The redraw clock the leading-edge throttle reads; tests replace it with a virtual one.
    /// Production uses UtcNow. Đồng hồ mà throttle leading-edge đọc; test thay bằng đồng hồ ảo.
    /// Production dùng UtcNow.
    /// </summary>
    internal Func<DateTime> DrawClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Test seam for the monitor work areas: when non-null, Start() uses these instead of querying
    /// user32. Production leaves it null. Điểm cho test các work area của monitor: khi khác null,
    /// Start() dùng chúng thay vì truy vấn user32. Production để null.
    /// </summary>
    internal (int Left, int Top, int Right, int Bottom)[]? WorkAreasOverride { get; set; }

    /// <summary>
    /// Start-up hook: creates the menu window (idempotent) and shows it at the persisted position —
    /// or the default top-right spot when none is stored — whenever FloatingMenuOn is set.
    /// Hook khởi động: tạo cửa sổ menu (idempotent) và hiện nó tại vị trí đã lưu — hoặc vị trí mặc
    /// định góc trên phải khi chưa có — khi FloatingMenuOn đang bật.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _workAreas = WorkAreasOverride ?? QueryMonitorWorkAreas(); // real areas once at start-up
            _host.CreateMenu(); // the window exists even when the menu starts hidden (idempotent)
            if (_settings.Load().FloatingMenuOn) ShowLocked(); // show only when the switch is on
        }
    }

    /// <summary>
    /// Shows or hides the floating menu, persisting FloatingMenuOn either way. Used by both the app
    /// switch and the tray item.
    /// Hiện hoặc ẩn menu nổi, lưu FloatingMenuOn trong cả hai trường hợp. Dùng chung bởi switch app
    /// và mục tray.
    /// </summary>
    /// <param name="on">Desired visibility. Trạng thái hiện mong muốn.</param>
    public void SetEnabled(bool on)
    {
        lock (_gate)
        {
            if (_disposed || _visible == on) return;
            if (on) ShowLocked(); else HideLocked();
            Persist(s => s with { FloatingMenuOn = on });
        }
    }

    /// <summary>
    /// Toggles the menu's visibility (tray "Show/hide floating menu").
    /// Bật/tắt việc hiện menu nổi (mục tray "Hiện/ẩn menu nổi").
    /// </summary>
    /// <returns>The new visibility state. Trạng thái hiện mới.</returns>
    public bool ToggleVisible()
    {
        lock (_gate)
        {
            if (_disposed) return _visible;
            if (_visible) HideLocked(); else ShowLocked();
            Persist(s => s with { FloatingMenuOn = _visible });
            return _visible;
        }
    }

    /// <summary>
    /// Starts the pipeline from the persisted source triple (spec D8–D10). A window source with no
    /// stored handle is refused with the error line plus a notification — the pipeline is NOT started
    /// and the settings are NOT mutated. RPC failures surface as the error line plus a notification;
    /// an ocr-model-missing rejection gets the friendly "model not installed" detail. The error line
    /// stays drawn until a later successful intent clears it — a passive repaint (e.g. the ~1 s stats
    /// tick) must not wipe it.
    /// Khởi động pipeline từ bộ ba nguồn đã lưu (spec D8–D10). Nguồn cửa sổ không có handle được từ
    /// chối bằng dòng lỗi kèm thông báo — KHÔNG khởi động pipeline và KHÔNG đổi cài đặt. Lỗi RPC hiện
    /// thành dòng lỗi kèm thông báo; lỗi thiếu model OCR nhận chi tiết thân thiện "chưa cài model".
    /// Dòng lỗi giữ nguyên cho đến khi một intent thành công sau đó xóa nó — một lần vẽ thụ động (vd
    /// tick thống kê ~1 s) không được phép xóa nó.
    /// </summary>
    /// <returns>A task that completes when the start attempt finishes (success or handled failure). Task hoàn tất khi nỗ lực start kết thúc (thành công hoặc thất bại đã xử lý).</returns>
    public async Task StartPipelineAsync()
    {
        string? refuse = null;
        SourceSelection? selection = null;
        lock (_gate)
        {
            if (_disposed) return;
            AppSettings s = _settings.Load();
            if (s.SourceUseWindow && string.IsNullOrWhiteSpace(s.SourceHwnd))
            {
                // D10: a stale window source must never silently fall back to monitor 0.
                // D10: nguồn cửa sổ cũ không được âm thầm rơi về màn hình 0.
                refuse = _L["menu.error.noWindow"];
            }
            else
            {
                selection = new SourceSelection(s.SourceUseWindow, s.SourceMonitor,
                    s.SourceUseWindow ? s.SourceHwnd : null);
            }
        }

        if (refuse is not null)
        {
            lock (_gate) { if (!_disposed) SetError(refuse); }
            return;
        }

        try
        {
            await _pipeline.StartAsync(selection!, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RpcException ex) when (!PipelineController.IsOcrModelMissing(ex))
        {
            // A failed start keeps the error line drawn — only the success path below clears it.
            // Start thất bại giữ dòng lỗi trên panel — chỉ nhánh thành công bên dưới mới xóa nó.
            lock (_gate) { if (!_disposed) SetError(_L.Fmt("menu.error.startFailed", ex.Message)); }
            return;
        }
        catch (RpcException ex) when (PipelineController.IsOcrModelMissing(ex))
        {
            // Friendly detail: the pinned key set has no dedicated model-missing string yet, so the
            // placeholder value is a hardcoded Vietnamese sentence until the post-Task-21 i18n pass.
            // Chi tiết thân thiện: bộ key ghim chưa có chuỗi riêng cho thiếu model nên giá trị chỗ đặt
            // là câu tiếng Việt hardcode đến lượt i18n sau Task 21.
            lock (_gate) { if (!_disposed) SetError(_L.Fmt("menu.error.startFailed", "model OCR chưa được cài")); }
            return;
        }
        catch (OperationCanceledException)
        {
            // Cancelled start (shutdown, user cancel): no error line, no notification.
            // Start bị hủy (app thoát, người dùng hủy): không dòng lỗi, không thông báo.
            return;
        }
        catch (Exception ex)
        {
            // A menu click must never take the worker thread down with an unobserved task exception.
            // Một cú click menu không được kéo sập worker thread bằng task exception không quan sát.
            _log.LogWarning(ex, "Pipeline start failed.");
            lock (_gate) { if (!_disposed) SetError(_L.Fmt("menu.error.startFailed", ex.Message)); }
            return;
        }

        lock (_gate)
        {
            if (_disposed) return;
            _errorLine = null; // reached only on success — a successful intent clears the previous error line
            RequestRedraw();
        }
    }

    /// <summary>
    /// Stops the current pipeline session (Core treats stop as idempotent). An RPC failure here is
    /// logged only — Core already owns the stopped state and the pinned key set has no stop-failure
    /// string, so reusing the start-failure line would mislead.
    /// Dừng phiên pipeline hiện tại (Core coi dừng là idempotent). Lỗi RPC ở đây chỉ được log — Core
    /// đã sở hữu trạng thái dừng và bộ key ghim không có chuỗi lỗi-dừng nên tái dùng dòng lỗi-start
    /// sẽ gây hiểu nhầm.
    /// </summary>
    /// <returns>A task that completes when the stop attempt finishes. Task hoàn tất khi nỗ lực dừng kết thúc.</returns>
    public async Task StopPipelineAsync()
    {
        bool ok = false;
        try
        {
            await _pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false);
            ok = true;
        }
        catch (RpcException ex)
        {
            _log.LogWarning(ex, "Pipeline stop RPC failed.");
        }
        catch (OperationCanceledException)
        {
            ok = true; // cancellation is not a failure
        }

        lock (_gate)
        {
            if (_disposed) return;
            if (ok) _errorLine = null;
            RequestRedraw();
        }
    }

    /// <summary>
    /// Toggles the local game overlay through the overlay service. No-op (no error) while the
    /// pipeline is not running — the control is disabled per spec §7.1.
    /// Bật/tắt lớp phủ game local qua service overlay. No-op (không báo lỗi) khi pipeline chưa chạy —
    /// control bị vô hiệu theo spec §7.1.
    /// </summary>
    public void ToggleOverlay()
    {
        bool running;
        lock (_gate)
        {
            if (_disposed) return;
            running = _pipeline.IsRunning; // plain property — safe to read under the gate
        }
        if (!running) return;

        // Deliberately OUTSIDE the gate: IsEnabled's getter and SetEnabled both take the overlay service's
        // own gate, and its save fires settings.Changed synchronously back into our RequestRedraw —
        // holding ours across that call would form an ABBA lock pair (menu gate ↔ overlay gate).
        // Cố ý NGOÀI gate: cả getter IsEnabled lẫn SetEnabled đều lấy gate riêng của service overlay, và
        // lần lưu của nó phát settings.Changed đồng bộ trở lại RequestRedraw của chúng ta — giữ gate của
        // mình xuyên suốt lời gọi ấy sẽ tạo cặp khóa ABBA (gate menu ↔ gate overlay).
        _overlay.SetEnabled(!_overlay.IsEnabled);
    }

    /// <summary>
    /// Cycles the destination language through the pinned list and persists immediately; the new
    /// pair applies on the next Start.
    /// Quay vòng ngôn ngữ đích qua danh sách ghim và lưu ngay; cặp mới áp dụng ở lần Start kế tiếp.
    /// </summary>
    /// <param name="forward">True for the next code, false for the previous (wrapping at both ends). True cho mã kế tiếp, false cho mã trước (vòng lại ở cả hai đầu).</param>
    public void CycleDst(bool forward) => CycleLanguage(forward, dst: true);

    /// <summary>
    /// Cycles the source language through the pinned list and persists immediately; applies on the
    /// next Start.
    /// Quay vòng ngôn ngữ nguồn qua danh sách ghim và lưu ngay; áp dụng ở lần Start kế tiếp.
    /// </summary>
    /// <param name="forward">True for the next code, false for the previous (wrapping at both ends). True cho mã kế tiếp, false cho mã trước (vòng lại ở cả hai đầu).</param>
    public void CycleSrc(bool forward) => CycleLanguage(forward, dst: false);

    /// <summary>
    /// Steps the OCR cadence by <paramref name="delta"/> clamped to 1..60 and persists immediately;
    /// applies on the next Start.
    /// Bước nhịp OCR theo delta, kẹp 1..60 và lưu ngay; áp dụng ở lần Start kế tiếp.
    /// </summary>
    /// <param name="delta">Change in frames per second (positive or negative). Thay đổi khung/giây (dương hoặc âm).</param>
    public void StepOcrFps(int delta) => StepFps(delta, ocr: true);

    /// <summary>
    /// Steps the capture cadence by <paramref name="delta"/> clamped to 1..60 and persists
    /// immediately; applies on the next Start.
    /// Bước nhịp chụp theo delta, kẹp 1..60 và lưu ngay; áp dụng ở lần Start kế tiếp.
    /// </summary>
    /// <param name="delta">Change in frames per second (positive or negative). Thay đổi khung/giây (dương hoặc âm).</param>
    public void StepCaptureFps(int delta) => StepFps(delta, ocr: false);

    /// <summary>
    /// Toggles multi-line OCR merging and persists immediately; applies on the next Start.
    /// Bật/tắt gộp nhiều dòng OCR và lưu ngay; áp dụng ở lần Start kế tiếp.
    /// </summary>
    public void ToggleMergeLines()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _errorLine = null;
            Persist(s => s with { OcrMergeLines = !s.OcrMergeLines });
        }
    }

    /// <summary>
    /// Opens or closes the stats number block under the Stats row (session-only, not persisted).
    /// Mở/đóng vùng số thống kê dưới row Thống kê (chỉ trong phiên, không lưu file).
    /// </summary>
    public void ToggleStats()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _errorLine = null;
            _statsOpen = !_statsOpen;
            RequestRedraw();
        }
    }

    private void CycleLanguage(bool forward, bool dst)
    {
        lock (_gate)
        {
            if (_disposed) return;
            string[] codes = dst ? DstCodes : SrcCodes;
            AppSettings current = _settings.Load();
            string next = codes[FloatingMenuLayout.CycleIndex(codes, dst ? current.Dst : current.Src, forward)];
            _errorLine = null;
            Func<AppSettings, AppSettings> update = dst ? (s => s with { Dst = next }) : (s => s with { Src = next });
            Persist(update);
        }
    }

    private void StepFps(int delta, bool ocr)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _errorLine = null;
            Func<AppSettings, AppSettings> update = ocr
                ? s => s with { OcrFps = FloatingMenuLayout.ClampFps(s.OcrFps + delta) }
                : s => s with { CaptureFps = FloatingMenuLayout.ClampFps(s.CaptureFps + delta) };
            Persist(update);
        }
    }

    /// <summary>
    /// Fetches the capturable sources (the ONLY caller of ListSourcesAsync — spec D11), builds the
    /// menu list and opens it under the Source row.
    /// Lấy các nguồn có thể capture (DUY NHẤT caller của ListSourcesAsync — spec D11), dựng danh sách
    /// và mở nó dưới row Nguồn.
    /// </summary>
    public async Task OpenSourceListAsync()
    {
        try
        {
            var (monitors, windows) = await _pipeline.ListSourcesAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed) return;
                _errorLine = null;
                // Monitor labels go through i18n (menu.source.monitor) so the list matches the active
                // language; window labels are built by the layout and never localized.
                // Label màn hình qua i18n (menu.source.monitor) để danh sách khớp ngôn ngữ hiện tại; label
                // cửa sổ do layout dựng và không bao giờ được dịch.
                _sources = FloatingMenuLayout.BuildSources(monitors, windows, i => _L.Fmt("menu.source.monitor", i + 1));
                _sourceListOpen = true;
                RequestRedraw();
            }
        }
        catch (RpcException ex)
        {
            // Interim: the pinned key set has no list-failure string yet — the start-failure line
            // carries the wire message until the post-Task-21 i18n pass.
            // Tạm thời: bộ key ghim chưa có chuỗi lỗi-liệt kê nên dùng dòng lỗi-start kèm thông điệp
            // trên wire đến lượt i18n sau Task 21.
            _log.LogWarning(ex, "ListSources failed.");
            lock (_gate) { if (!_disposed) SetError(_L.Fmt("menu.error.startFailed", ex.Message)); }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Selects a capture source from the open list: persists the triple immediately (monitor →
    /// SourceMonitor, window → SourceUseWindow + SourceHwnd), closes the list and does NOT stop a
    /// running pipeline — the new source applies on the next Start.
    /// Chọn nguồn capture từ danh sách đang mở: lưu bộ ba ngay (màn hình → SourceMonitor, cửa sổ →
    /// SourceUseWindow + SourceHwnd), đóng danh sách và KHÔNG Stop pipeline đang chạy — nguồn mới áp
    /// dụng ở lần Start kế tiếp.
    /// </summary>
    /// <param name="item">The chosen source item. Mục nguồn được chọn.</param>
    public void SelectSource(SourceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_disposed) return;
            _errorLine = null;
            _sourceListOpen = false;
            Func<AppSettings, AppSettings> update = item.IsMonitor
                ? s => s with { SourceUseWindow = false, SourceMonitor = item.Id }
                : s => s with { SourceUseWindow = true, SourceHwnd = item.HwndHex };
            Persist(update);
        }
    }

    /// <summary>
    /// "Turn off floating menu" row: hides the window and persists FloatingMenuOn=false.
    /// Row "Tắt menu nổi": ẩn cửa sổ và lưu FloatingMenuOn=false.
    /// </summary>
    public void CloseMenu() => SetEnabled(false);

    /// <summary>
    /// Handles a click inside the panel (host event, PANEL coordinates): converts to window
    /// coordinates by adding the shadow margin, hit-tests and dispatches the matching intent. A chip
    /// click expands and persists; null hits (inert zones) are ignored.
    /// Xử lý click trong panel (event host, TOẠ ĐỘ PANEL): đổi sang toạ độ window bằng cách cộng viền
    /// shadow, hit-test và dispatch intent khớp. Click chip thì mở rộng và lưu; hit null (vùng trơ) bị bỏ qua.
    /// </summary>
    /// <param name="x">Click X in panel coordinates (origin = panel top-left). Toạ độ X click trong hệ panel (gốc = góc trên trái panel).</param>
    /// <param name="y">Click Y in panel coordinates. Toạ độ Y click trong hệ panel.</param>
    public void OnPanelClicked(int x, int y)
    {
        lock (_gate)
        {
            if (_disposed || !_visible) return;
            // Interpret the click against what the user can SEE: a throttled draw may leave the painted
            // panel behind the internal state (e.g. an opened source list whose frame was dropped), and
            // hit-testing fresh state would dispatch to rows that were never painted.
            // Đọc cú click theo đúng thứ NGƯỜI DÙNG THẤY: một lần vẽ bị throttle có thể để panel đã vẽ
            // tụt sau state nội bộ (vd danh sách nguồn mở mà khung hình bị bỏ), và hit-test state mới sẽ
            // dispatch vào các row chưa từng được vẽ.
            MenuState state = LastDrawnState ?? BuildState();
            MenuHit? hit = FloatingMenuLayout.HitTest(x + FloatingMenuLayout.ShadowMargin,
                y + FloatingMenuLayout.ShadowMargin, state);
            if (hit is null) return;

            switch (hit.Value.RowId)
            {
                case FloatingMenuLayout.Chip:
                    _errorLine = null;
                    Persist(s => s with { FloatingMenuCollapsed = false });
                    break;

                case FloatingMenuLayout.CollapseButton:
                    _errorLine = null;
                    Persist(s => s with { FloatingMenuCollapsed = true });
                    break;

                case FloatingMenuLayout.RowStartStop:
                    // Fire-and-forget on the worker thread: both intents catch every failure of their
                    // own, so no exception escapes into the host's event dispatch.
                    // Gọi lửa-rồi-để trên worker thread: cả hai intent tự bắt hết lỗi của mình nên
                    // không exception nào thoát ra ngoài dispatch event của host.
                    _ = state.Running ? StopPipelineAsync() : StartPipelineAsync();
                    break;

                case FloatingMenuLayout.RowSource:
                    _ = OpenSourceListAsync();
                    break;

                case FloatingMenuLayout.RowOverlay:
                    ToggleOverlay();
                    break;

                case FloatingMenuLayout.RowDst:
                    CycleDst(true);
                    break;

                case FloatingMenuLayout.RowSrc:
                    CycleSrc(true);
                    break;

                case FloatingMenuLayout.RowOcrFps:
                    if (hit.Value.Zone == MenuZone.Left) StepOcrFps(-1);
                    else if (hit.Value.Zone == MenuZone.Right) StepOcrFps(+1);
                    break;

                case FloatingMenuLayout.RowCaptureFps:
                    if (hit.Value.Zone == MenuZone.Left) StepCaptureFps(-1);
                    else if (hit.Value.Zone == MenuZone.Right) StepCaptureFps(+1);
                    break;

                case FloatingMenuLayout.RowMergeLines:
                    ToggleMergeLines();
                    break;

                case FloatingMenuLayout.RowStats:
                    ToggleStats();
                    break;

                case FloatingMenuLayout.RowCloseMenu:
                    CloseMenu();
                    break;

                default:
                    if (hit.Value.RowId >= FloatingMenuLayout.SourceItemBase)
                    {
                        int index = hit.Value.RowId - FloatingMenuLayout.SourceItemBase;
                        if (index < state.Sources.Count) SelectSource(state.Sources[index]);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Handles the end of a drag (host event): clamps the new window position into the work area,
    /// persists FloatingMenuX/Y and repositions the window when the clamp moved it.
    /// Xử lý kết thúc kéo (event host): kẹp vị trí window mới vào work area, lưu FloatingMenuX/Y và
    /// đặt lại cửa sổ khi phép kẹp làm nó dịch chuyển.
    /// </summary>
    /// <param name="x">New window left edge in desktop physical pixels. Mép trái mới của window theo pixel vật lý desktop.</param>
    /// <param name="y">New window top edge in desktop physical pixels. Mép trên mới của window theo pixel vật lý desktop.</param>
    public void OnDragFinished(int x, int y)
    {
        lock (_gate)
        {
            if (_disposed || !_visible) return;
            (int cx, int cy) = FloatingMenuLayout.ClampPosition(x, y, _windowW, _windowH, _workAreas);
            _posX = cx;
            _posY = cy;
            Persist(s => s with { FloatingMenuX = cx, FloatingMenuY = cy });
            if (cx != x || cy != y)
                _host.ShowMenu(cx, cy, _windowW, _windowH); // the drag overshot the work area — clamp back
        }
    }

    /// <summary>
    /// Unsubscribes all change events and disposes the cached render surfaces. Idempotent.
    /// Hủy đăng ký mọi event thay đổi và dispose các bề mặt render đã cache. Idempotent.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pipeline.StateChanged -= OnRunStateChanged;
            _pipeline.PipelineStateChanged -= OnPipelineLoopChanged;
            _settings.Changed -= OnSettingsChanged;
            _stats.Changed -= OnStatsChanged;
            foreach (SD.Bitmap surface in _surfaces.Values) surface.Dispose();
            _surfaces.Clear();
        }
    }

    // All four change sources funnel into the same leading-edge-throttled redraw — none may bypass
    // it, that is the whole point of Review Focus #6 (redraw storm). Each handler isolates its own
    // failures: RunStats raises from a timer callback where an unobserved exception kills the process,
    // and a throwing subscriber on any source must not take down that source's pump.
    // Cả bốn nguồn thay đổi đều đổ về một đường redraw throttle leading-edge duy nhất — không nguồn
    // nào được phép bỏ qua nó, đó chính là ý của Review Focus #6 (bão redraw). Mỗi handler tự cách ly
    // lỗi của mình: RunStats phát từ callback timer, nơi exception không quan sát sẽ giết tiến trình,
    // và một subscriber ném lỗi trên bất kỳ nguồn nào cũng không được kéo sập vòng chạy của nguồn đó.
    private void OnRunStateChanged() => SafeRequestRedraw("pipeline state change");
    private void OnPipelineLoopChanged(bool running, string? reason) => SafeRequestRedraw("pipeline loop change");
    private void OnSettingsChanged() => SafeRequestRedraw("settings change");
    private void OnStatsChanged() => SafeRequestRedraw("stats change");

    /// <summary>
    /// Passive redraw with isolation: a throwing subscriber on any of the four change sources must not
    /// take down its pump (RunStats raises from a timer callback, where an exception kills the process).
    /// Redraw thụ động có cách ly: một subscriber ném lỗi trên bất kỳ nguồn đổi nào cũng không được kéo
    /// sập vòng chạy của nó (RunStats phát từ callback timer, nơi exception sẽ giết tiến trình).
    /// </summary>
    /// <param name="source">Name of the change source, for the log line. Tên nguồn thay đổi, để ghi log.</param>
    private void SafeRequestRedraw(string source)
    {
        try
        {
            RequestRedraw();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Passive floating-menu redraw skipped ({Source}).", source);
        }
    }

    /// <summary>
    /// Shows the menu at the persisted position (clamped into the work area) or the default spot —
    /// top-right of the primary monitor — sized to the current state, and paints the initial frame.
    /// The caller decides whether showing is wanted: Start() gates on FloatingMenuOn, SetEnabled and
    /// ToggleVisible have already made the call (re-showing after a hide must work even though the
    /// persisted flag still reads off).
    /// Hiện menu tại vị trí đã lưu (kẹp vào work area) hoặc vị trí mặc định — góc trên phải màn hình
    /// chính — đúng cỡ trạng thái hiện tại, và vẽ khung hình đầu tiên. Caller quyết định có nên hiện
    /// hay không: Start() tự kiểm tra FloatingMenuOn, SetEnabled và ToggleVisible đã tự quyết (hiện lại
    /// sau khi ẩn phải hoạt động dù cờ đã lưu vẫn đang off).
    /// </summary>
    private void ShowLocked() // caller holds _gate — the caller has already decided to show
    {
        if (_visible) return;
        AppSettings s = _settings.Load();

        MenuState state = BuildState();
        var (w, h) = FloatingMenuLayout.WindowSize(state);
        var (x, y) = s.FloatingMenuX is int px && s.FloatingMenuY is int py
            ? FloatingMenuLayout.ClampPosition(px, py, w, h, _workAreas)
            : FloatingMenuLayout.DefaultPosition(w, h, _workAreas[0]); // primary monitor first

        _visible = true;
        _posX = x;
        _posY = y;
        _windowW = w; // ShowMenu below already sized the window — DrawLocked must not re-issue it
        _windowH = h;
        _host.ShowMenu(x, y, w, h);
        DrawLocked(state); // a just-shown window must not be blank — bypasses the throttle
    }

    /// <summary>Hides the menu window without destroying it. Ẩn cửa sổ menu mà không hủy nó.</summary>
    private void HideLocked() // caller holds _gate
    {
        if (!_visible) return;
        _visible = false;
        _host.HideMenu();
    }

    /// <summary>
    /// Leading-edge throttle: the first event after a draw paints immediately; every event inside
    /// <see cref="_redrawInterval"/> of it is dropped. No trailing timer exists — if state changes
    /// again later, that change's own event redraws. A fresh snapshot identical to the last DRAWN one
    /// is skipped without painting or moving the throttle anchor, so a per-second stats tick with
    /// unchanged numbers costs a record comparison, not a GDI+ render plus host push.
    /// Throttle leading-edge: sự kiện đầu sau một lần vẽ vẽ ngay; mọi sự kiện nằm trong cửa sổ
    /// _redrawInterval kế đó bị bỏ. Không có trailing timer — nếu state đổi lại sau đó, chính sự kiện
    /// của lần đổi ấy sẽ vẽ. Snapshot mới trùng snapshot đã vẽ gần nhất bị bỏ qua mà không vẽ và không
    /// dời mốc throttle, nên tick thống kê mỗi giây với số không đổi chỉ tốn một phép so record chứ
    /// không tốn render GDI+ cộng đẩy host.
    /// </summary>
    private void RequestRedraw()
    {
        lock (_gate)
        {
            if (_disposed || !_visible) return;
            DateTime now = DrawClock();
            if (now - _lastDrawUtc < _redrawInterval) return;
            MenuState state = BuildState();
            if (state == LastDrawnState) return; // no real change — skip the render, keep the anchor
            DrawLocked(state);
        }
    }

    /// <summary>
    /// Renders the state into a cached per-size surface and pushes it to the host, resizing the
    /// window in place when the state's size changed (stats block open/close, error line appearing).
    /// Vẽ state vào bề mặt cache theo cỡ và đẩy cho host, đổi cỡ window tại chỗ khi cỡ của state thay
    /// đổi (mở/đóng vùng thống kê, dòng lỗi xuất hiện).
    /// </summary>
    private void DrawLocked(MenuState state) // caller holds _gate
    {
        _lastDrawUtc = DrawClock(); // a real draw — anchor the throttle window here (initial paint included)
        var (w, h) = FloatingMenuLayout.WindowSize(state);
        if (w != _windowW || h != _windowH)
        {
            _windowW = w;
            _windowH = h;
            _host.ShowMenu(_posX, _posY, w, h);
        }

        SD.Bitmap surface = GetSurface(w, h);
        using (SD.Graphics g = SD.Graphics.FromImage(surface))
        {
            g.SmoothingMode = SDD.SmoothingMode.AntiAlias;
            g.TextRenderingHint = SDT.TextRenderingHint.AntiAlias;
            g.Clear(SD.Color.Transparent);
            FloatingMenuRenderer.DrawMenu(g, state, w, h, _L);
        }

        _host.DrawMenu(surface);
        LastDrawnState = state;
    }

    /// <summary>
    /// The host consumes bitmaps asynchronously on its worker thread, so a per-draw bitmap would be
    /// disposed before it was pushed (or leak if never disposed). One surface per window size is
    /// reused for the service's lifetime; at most a few sizes exist at once, so this costs a couple
    /// of hundred KB.
    /// Host tiêu thụ bitmap bất đồng bộ trên worker thread, nên bitmap vẽ-mỗi-lần sẽ bị dispose trước
    /// khi kịp đẩy (hoặc rò rỉ nếu không dispose). Một bề mặt cho mỗi cỡ window được tái sử dụng suốt
    /// tuổi thọ service; số cỡ tồn tại cùng lúc chỉ vài cái nên chi phí chừng vài trăm KB.
    /// </summary>
    private SD.Bitmap GetSurface(int w, int h) // caller holds _gate
    {
        if (!_surfaces.TryGetValue((w, h), out var surface))
        {
            surface = new SD.Bitmap(w, h, SDI.PixelFormat.Format32bppPArgb);
            _surfaces[(w, h)] = surface;
        }
        return surface;
    }

    /// <summary>
    /// Fresh snapshot from the persisted settings (including the overlay switch — the overlay service
    /// persists OverlayOnGame on every toggle) and the live pipeline state and RunStats.
    /// Snapshot mới từ cài đặt đã lưu (kể cả công tắc lớp phủ — service overlay persist OverlayOnGame ở
    /// mỗi lần bật/tắt) cùng trạng thái pipeline đang chạy và RunStats.
    /// </summary>
    private MenuState BuildState() // caller holds _gate
    {
        AppSettings s = _settings.Load();
        return new MenuState(
            Collapsed: s.FloatingMenuCollapsed,
            Running: _pipeline.IsRunning,
            SourceLabel: SourceLabelFor(s),
            OverlayOn: s.OverlayOnGame, // persisted per toggle — reading _overlay.IsEnabled here would cross into its gate
            Dst: s.Dst,
            Src: s.Src,
            OcrFps: s.OcrFps,
            CaptureFps: s.CaptureFps,
            MergeLines: s.OcrMergeLines,
            StatsOpen: _statsOpen,
            SourceListOpen: _sourceListOpen,
            Sources: _sources,
            ErrorLine: _errorLine,
            Fps: _stats.Fps,
            BoxCount: _stats.BoxCount,
            TranslatedLineCount: _stats.TranslatedLineCount);
    }

    /// <summary>
    /// The source label for the menu: the live session's source when one is active, otherwise the
    /// persisted triple. A window source shows its list label ("{Process} — {Title}") when the last
    /// fetched source list still knows it, else the raw handle; a monitor reads "Màn hình {N+1}"
    /// exactly like FloatingMenuLayout.BuildSources renders it.
    /// Nhãn nguồn cho menu: nguồn của phiên đang chạy nếu có, ngược lại là bộ ba đã lưu. Nguồn cửa sổ
    /// hiện label trong danh sách ("{Process} — {Title}") khi danh sách nguồn gần nhất còn biết nó,
    /// nếu không thì handle thô; màn hình đọc "Màn hình {N+1}" đúng như BuildSources của layout render.
    /// </summary>
    private string SourceLabelFor(AppSettings s) // caller holds _gate
    {
        SourceSelection? active = _pipeline.ActiveSource;
        bool useWindow = active is not null ? active.UseWindow : s.SourceUseWindow;
        if (useWindow)
        {
            string? hwnd = active?.HwndHex ?? s.SourceHwnd;
            if (string.IsNullOrWhiteSpace(hwnd)) return "(chưa chọn cửa sổ)";
            foreach (SourceItem item in _sources)
                if (!item.IsMonitor && string.Equals(item.HwndHex, hwnd, StringComparison.Ordinal))
                    return item.Label;
            return $"Cửa sổ {hwnd}";
        }

        int monitor = active?.Monitor ?? s.SourceMonitor;
        return $"Màn hình {monitor + 1}";
    }

    /// <summary>
    /// Read-modify-write under the gate so concurrent savers cannot lose updates; a failed save is
    /// logged, not thrown — an intent must not die because the disk refused (same pattern as
    /// OverlayWindowService.Persist).
    /// Đọc-sửa-ghi dưới gate để các thread lưu song song không mất cập nhật; lỗi ghi được log chứ
    /// không ném — intent không được chết chỉ vì đĩa từ chối (cùng pattern như Persist của
    /// OverlayWindowService).
    /// </summary>
    private void Persist(Func<AppSettings, AppSettings> update) // caller holds _gate
    {
        try
        {
            _settings.Save(update(_settings.Load()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist floating-menu settings.");
        }
    }

    /// <summary>
    /// Sets the panel's short error line and routes the same message through the shared notifier
    /// (toast when the app is focused, tray balloon otherwise) — spec D12.
    /// Đặt dòng lỗi ngắn của panel và đưa cùng thông điệp qua đường thông báo chung (toast khi app
    /// đang focus, balloon tray ngược lại) — spec D12.
    /// </summary>
    private void SetError(string message) // caller holds _gate
    {
        _errorLine = message;
        try
        {
            _notifier.Notify(message, ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            // The toast branch is NOT isolated (AppNotifier documents it): a throwing subscriber must
            // not kill the intent's redraw bookkeeping.
            // Nhánh toast KHÔNG được cách ly (AppNotifier ghi rõ): một subscriber ném lỗi không được
            // kéo sập phần ghi sổ redraw của intent.
            _log.LogWarning(ex, "Floating-menu error notification failed.");
        }
        RequestRedraw();
    }

    /// <summary>
    /// Queries user32 for the work area (rcWork) of every monitor, primary monitor first; returns the
    /// layout fallback when the query yields nothing. Classic DllImport is used (not LibraryImport) —
    /// same convention as Win32AnchorTracker and ReTran.Core.Capture.NativeMethods.
    /// Truy vấn user32 lấy work area (rcWork) của mọi monitor, màn hình chính trước; trả về vùng dự
    /// phòng của layout khi truy vấn không về gì. Dùng DllImport cổ điển (không phải LibraryImport) —
    /// cùng quy ước với Win32AnchorTracker và ReTran.Core.Capture.NativeMethods.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom)[] QueryMonitorWorkAreas()
    {
        var primary = new List<(int Left, int Top, int Right, int Bottom)>();
        var others = new List<(int Left, int Top, int Right, int Bottom)>();
        List<(int Left, int Top, int Right, int Bottom)> TargetOf(bool isPrimary) => isPrimary ? primary : others;

        NativeMethods.MonitorEnumProc callback = (nint monitor, nint _, ref NativeMethods.RECT __, nint _) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfoW(monitor, ref info))
                TargetOf((info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
                    .Add((info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom));
            return true; // keep enumerating — every monitor counts
        };

        bool ok = NativeMethods.EnumDisplayMonitors(nint.Zero, nint.Zero, callback, nint.Zero);
        GC.KeepAlive(callback); // the delegate must stay alive across the P/Invoke call

        if (!ok || primary.Count + others.Count == 0) return [FloatingMenuLayout.FallbackWorkArea];

        var areas = new List<(int Left, int Top, int Right, int Bottom)>(primary.Count + others.Count);
        areas.AddRange(primary); // primary monitor first
        areas.AddRange(others);
        return [.. areas];
    }

    /// <summary>
    /// user32 entry points for the work-area query (internal only). ReTran.App has no Forms reference,
    /// so Screen.AllScreens is not an option; Win32AnchorTracker's own enumeration is private and
    /// reports rcMonitor instead of rcWork.
    /// Các hàm user32 cho truy vấn work area (chỉ nội bộ). ReTran.App không tham chiếu Forms nên
    /// Screen.AllScreens không phải lựa chọn; phép liệt kê của Win32AnchorTracker là private và trả
    /// rcMonitor chứ không phải rcWork.
    /// </summary>
    private static class NativeMethods
    {
        internal const int MONITORINFOF_PRIMARY = 1;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            internal int cbSize;
            internal RECT rcMonitor;
            internal RECT rcWork;
            internal int dwFlags;
        }

        internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);
    }
}
