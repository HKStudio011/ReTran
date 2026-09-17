namespace ReTran_App.Core;

/// <summary>
/// A startable/stoppable source of overlay frames.
/// Một nguồn khung overlay có thể khởi động/dừng.
/// </summary>
public interface IOverlayFeed : IDisposable
{
    /// <summary>
    /// Starts publishing frames. No-op when already running.
    /// Bắt đầu xuất bản khung hình. Không làm gì khi đã chạy.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops publishing frames. Idempotent.
    /// Dừng xuất bản khung hình. Gọi nhiều lần vẫn an toàn.
    /// </summary>
    void Stop();
}

/// <summary>
/// Timer-based <see cref="IOverlayFeed"/> publishing two moving demo boxes for UI verification.
/// <see cref="IOverlayFeed"/> chạy bằng timer, xuất bản hai hộp demo di chuyển để kiểm chứng UI.
/// </summary>
/// <param name="state">The overlay state to publish into. / Trạng thái overlay để xuất bản vào.</param>
/// <param name="intervalMs">Timer interval in milliseconds (default 500, minimum 50). / Chu kỳ timer theo mili-giây (mặc định 500, tối thiểu 50).</param>
public sealed class DemoFeed(OverlayState state, int intervalMs = 500) : IOverlayFeed
{
    private readonly OverlayState _state = state ?? throw new ArgumentNullException(nameof(state));
    private readonly int _intervalMs = intervalMs >= 50
        ? intervalMs
        : throw new ArgumentOutOfRangeException(nameof(intervalMs), "Interval must be at least 50ms. / Chu kỳ phải ít nhất 50ms.");
    private readonly object _gate = new();
    private Timer? _timer;
    private int _tick;
    private bool _disposed;

    /// <summary>
    /// Starts the timer. No-op when already running.
    /// Khởi động timer. Không làm gì khi đã chạy.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the feed was disposed. / Ném ra khi feed đã bị hủy.</exception>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timer is not null) return;
            _timer = new Timer(Tick, null, _intervalMs, _intervalMs);
        }
    }

    /// <summary>
    /// Stops the timer. Idempotent.
    /// Dừng timer. Gọi nhiều lần vẫn an toàn.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>
    /// Stops the feed and releases the timer.
    /// Dừng feed và giải phóng timer.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick(object? _)
    {
        int tick = Interlocked.Increment(ref _tick);
        double dx = 0.05 * (tick % 10);
        _state.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow, new[]
        {
            new OverlayBox(0.10 + dx, 0.20, 0.25, 0.07, "こんにちは", "Xin chào"),
            new OverlayBox(0.65 - dx, 0.50, 0.20, 0.07, "スタート", "Bắt đầu"),
        }));
    }
}
