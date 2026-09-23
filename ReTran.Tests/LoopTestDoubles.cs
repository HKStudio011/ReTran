using System.Text;
using ReTran.Core.Capture;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

/// <summary>
/// Fake capture source: frames queued before Start() flush on Start; later raises fire immediately.
/// Capture source giả: frame xếp hàng trước Start sẽ phát lúc Start; raise sau đó phát ngay.
/// </summary>
internal sealed class FakeCaptureSource : ICaptureSource
{
    private readonly Queue<Frame> _pending = new();

    public CaptureTarget Target { get; } = new ScreenTarget(0);
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    public bool IsRunning { get; private set; }

    public void Start()
    {
        IsRunning = true;
        lock (_pending)
        {
            while (_pending.Count > 0)
                FrameArrived?.Invoke(this, new FrameArrivedEventArgs(_pending.Dequeue()));
        }
    }

    public void Stop() => IsRunning = false;

    public void Dispose()
    {
        lock (_pending)
        {
            while (_pending.Count > 0)
                _pending.Dequeue().Dispose();
        }
    }

    /// <summary>Queues (pre-Start) or raises a frame. Xếp hàng (trước Start) hoặc phát frame.</summary>
    public void Raise(Frame frame)
    {
        lock (_pending)
        {
            if (IsRunning)
                FrameArrived?.Invoke(this, new FrameArrivedEventArgs(frame));
            else
                _pending.Enqueue(frame);
        }
    }
}

/// <summary>
/// Thread-safe line-capturing writer with a wait-for-N-lines handshake.
/// Ghi dòng an toàn luồng kèm chờ đủ N dòng.
/// </summary>
internal sealed class SyncTextWriter : TextWriter
{
    private readonly object _lock = new();
    private readonly List<string> _lines = [];

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            _lines.Add(value ?? "");
            Monitor.PulseAll(_lock);
        }
    }

    public string[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    /// <summary>Waits until at least <paramref name="count"/> lines were written (false on timeout).</summary>
    public Task<bool> WaitForLineCount(int count, TimeSpan timeout) => Task.Run(() =>
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        lock (_lock)
        {
            while (_lines.Count < count)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return false;
                Monitor.Wait(_lock, remaining);
            }
            return true;
        }
    });
}
