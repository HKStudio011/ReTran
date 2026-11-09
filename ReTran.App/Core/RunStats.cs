namespace ReTran_App.Core;

/// <summary>
/// Shared run statistics for the active pipeline session: preview fps, current box count and
/// translated-line count. Every UI surface (control page, floating menu) reads this single source
/// instead of computing its own numbers — one fps formula for the whole app.
/// Thống kê dùng chung của phiên pipeline đang chạy: fps preview, số box và số dòng dịch hiện tại.
/// Mọi bề mặt UI (trang điều khiển, menu nổi) đọc chung nguồn này thay vì tự tính số riêng — một
/// công thức fps duy nhất cho toàn app.
/// </summary>
/// <remarks>
/// Fps is counted on PREVIEW frames (<c>frame.annotated</c>) over a fixed window — the same source
/// and formula the control page used before this type existed: one <see cref="Interlocked.Increment"/>
/// per preview frame, then <c>frames / elapsed</c> on a 1 s threadpool timer. Box/line counts follow
/// <see cref="OverlayState.Latest"/>. All values reset to zero when the pipeline stops — both stop
/// paths: an abrupt stop (<see cref="IPipelineControl.PipelineStateChanged"/>) and a user Stop
/// (<see cref="IPipelineControl.StateChanged"/> with <c>IsRunning == false</c>).
/// Fps đếm trên khung PREVIEW (<c>frame.annotated</c>) trong cửa sổ cố định — cùng nguồn và công thức
/// trang điều khiển đã dùng trước khi có type này: mỗi khung preview một lần
/// <see cref="Interlocked.Increment"/>, rồi <c>frames / elapsed</c> trên timer threadpool 1 s. Số
/// box/dòng dịch theo <see cref="OverlayState.Latest"/>. Mọi giá trị về 0 khi pipeline dừng — cả hai
/// đường: dừng đột xuất (<see cref="IPipelineControl.PipelineStateChanged"/>) và Stop do người dùng
/// (<see cref="IPipelineControl.StateChanged"/> với <c>IsRunning == false</c>).
/// </remarks>
public sealed class RunStats : IDisposable
{
    /// <summary>Default fps window / timer interval: 1 s. Cửa sổ đo fps / chu kỳ timer mặc định: 1 s.</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Minimum gap between two <see cref="Changed"/> raises — leading edge, no trailing timer.
    /// Khoảng tối thiểu giữa hai lần phát <see cref="Changed"/> — leading-edge, không có trailing timer.
    /// </summary>
    private const long ChangedMinIntervalMs = 250;

    private readonly PreviewState _preview;
    private readonly OverlayState _overlay;
    private readonly IPipelineControl _pipeline;
    private readonly Func<long> _now;
    private readonly Timer? _timer;

    private int _frames;
    private long _windowStart;
    private long _lastChanged = long.MinValue;
    private double _fps;
    private int _boxCount;
    private int _translatedLineCount;
    private bool _disposed;

    /// <summary>
    /// Creates the stats and subscribes to preview, overlay and pipeline state.
    /// Tạo thống kê và đăng ký event preview, overlay cùng trạng thái pipeline.
    /// </summary>
    /// <param name="preview">Preview frame source — fps is counted on its frames. Nguồn khung preview — fps đếm trên khung của nó.</param>
    /// <param name="overlay">Overlay frame source — box/line counts read from <see cref="OverlayState.Latest"/>. Nguồn khung overlay — số box/dòng đọc từ <see cref="OverlayState.Latest"/>.</param>
    /// <param name="pipeline">Pipeline surface providing the run state and stop signals. Bề mặt pipeline cung cấp trạng thái chạy và tín hiệu dừng.</param>
    /// <param name="now">
    /// Millisecond clock; defaults to <see cref="Environment.TickCount64"/>. When injected, no real
    /// timer is created — tests drive ticks through the internal <c>TickForTest</c>.
    /// Đồng hồ mili giây; mặc định <see cref="Environment.TickCount64"/>. Khi inject thì không tạo
    /// timer thật — test tự gọi <c>TickForTest</c> nội bộ để chạy tick.
    /// </param>
    /// <param name="window">Fps measurement window and timer interval; defaults to 1 s. Cửa sổ đo fps và chu kỳ timer; mặc định 1 s.</param>
    public RunStats(PreviewState preview, OverlayState overlay, IPipelineControl pipeline,
        Func<long>? now = null, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(pipeline);
        _preview = preview;
        _overlay = overlay;
        _pipeline = pipeline;
        Func<long> defaultClock = () => Environment.TickCount64;
        _now = now ?? defaultClock;

        TimeSpan effectiveWindow = window ?? DefaultWindow;
        if (effectiveWindow <= TimeSpan.Zero) effectiveWindow = DefaultWindow;

        _windowStart = _now();
        _preview.Changed += OnPreviewChanged;
        _overlay.Changed += OnOverlayChanged;
        pipeline.StateChanged += OnStateChanged;
        pipeline.PipelineStateChanged += OnPipelineStateChanged;

        // Clock injected (test): no real timer — tests call TickForTest() directly.
        // Inject clock (test): không tạo timer thật — test gọi TickForTest() trực tiếp.
        _timer = now is null
            ? new Timer(static self => ((RunStats)self!).Tick(), this, effectiveWindow, effectiveWindow)
            : null;
    }

    /// <summary>
    /// Preview frames per second over the last measurement window; 0 while the pipeline is not running.
    /// Số khung preview mỗi giây trong cửa sổ đo vừa qua; 0 khi pipeline không chạy.
    /// </summary>
    public double Fps => _pipeline.IsRunning ? _fps : 0.0;

    /// <summary>
    /// Text boxes on the latest overlay frame; 0 before the first frame and after a stop.
    /// Số hộp văn bản trên khung overlay mới nhất; 0 trước khung đầu tiên và sau khi dừng.
    /// </summary>
    public int BoxCount => _boxCount;

    /// <summary>
    /// Boxes on the latest overlay frame carrying a non-empty translation.
    /// Số hộp trên khung overlay mới nhất có bản dịch không rỗng.
    /// </summary>
    public int TranslatedLineCount => _translatedLineCount;

    /// <summary>
    /// Raised when any published value changes, at most once per 250 ms (leading edge, no trailing
    /// timer). The 1 s timer tick always attempts to raise, so consumers observe a stop reset within
    /// one window even if the stop event itself was throttled.
    /// Được phát khi một giá trị xuất bản thay đổi, tối đa một lần mỗi 250 ms (leading-edge, không
    /// trailing timer). Tick timer 1 s luôn thử phát, nên consumer thấy reset-dừng trong ≤1 cửa sổ
    /// kể cả khi chính sự kiện dừng bị throttle.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Runs one fps measurement tick manually — the test seam used when a clock is injected (no real
    /// timer exists then). Chạy một tick đo fps thủ công — điểm cho test khi đã inject clock (không
    /// có timer thật lúc đó).
    /// </summary>
    internal void TickForTest() => Tick();

    /// <summary>
    /// Unsubscribes from the preview, overlay and pipeline events and disposes the timer (when one was created).
    /// Hủy đăng ký event preview, overlay và pipeline, dispose timer (nếu đã tạo).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preview.Changed -= OnPreviewChanged;
        _overlay.Changed -= OnOverlayChanged;
        _pipeline.StateChanged -= OnStateChanged;
        _pipeline.PipelineStateChanged -= OnPipelineStateChanged;
        _timer?.Dispose();
    }

    // frame.annotated arrives on the peer read-loop thread — count it first, exactly how the control
    // page counted preview fps (fps của PREVIEW, không phải khung translated).
    private void OnPreviewChanged(PreviewFrame frame)
    {
        if (_disposed) return;
        Interlocked.Increment(ref _frames);
        RaiseChanged();
    }

    // frame.translated ~3 fps — cheap enough to read box/line counts, same formula as the page.
    private void OnOverlayChanged()
    {
        if (_disposed) return;
        OverlayFrame? frame = _overlay.Latest;
        _boxCount = frame?.Boxes.Count ?? 0;
        _translatedLineCount = frame?.Boxes.Count(b => !string.IsNullOrWhiteSpace(b.Translated)) ?? 0;
        RaiseChanged();
    }

    // A user Stop (PipelineController.StopAsync) raises ONLY StateChanged — not PipelineStateChanged.
    private void OnStateChanged()
    {
        if (_disposed) return;
        if (_pipeline.IsRunning)
        {
            // New session: restart the fps window (the page reset _fpsFrames/_fpsWindowStart on start).
            Interlocked.Exchange(ref _frames, 0);
            _windowStart = _now();
        }
        else
        {
            Reset();
        }
    }

    private void OnPipelineStateChanged(bool running, string? reason)
    {
        if (_disposed) return;
        if (!running) Reset(); // abrupt stop — both events fire; Reset is idempotent (dừng đột xuất — cả hai event nổ; Reset idempotent)
    }

    private void Reset()
    {
        _fps = 0.0;
        _boxCount = 0;
        _translatedLineCount = 0;
        Interlocked.Exchange(ref _frames, 0);
        _windowStart = _now();
        RaiseChanged();
    }

    // Threadpool timer tick — same formula as the page's old TickFps: fps = frames / elapsed over the
    // window just passed, then the counter and the window start move on.
    private void Tick()
    {
        if (_disposed) return;
        long now = _now();
        int frames = Interlocked.Exchange(ref _frames, 0);
        double elapsed = Math.Max(0.001, (now - _windowStart) / 1000.0);
        _windowStart = now;
        _fps = _pipeline.IsRunning ? frames / elapsed : 0.0;
        RaiseChanged(); // always attempt (throttled): consumers see a stop reset within one window (luôn thử phát (đã throttle): consumer thấy reset-dừng trong ≤1 cửa sổ)
    }

    // Leading-edge throttle: at most one raise per 250 ms, no trailing timer. The CAS keeps the edge
    // strict even when two event threads race.
    private void RaiseChanged()
    {
        long now = _now();
        while (true)
        {
            long last = Interlocked.Read(ref _lastChanged);
            if (last != long.MinValue && now - last < ChangedMinIntervalMs) return;
            if (Interlocked.CompareExchange(ref _lastChanged, now, last) == last) break;
        }
        Changed?.Invoke();
    }
}
