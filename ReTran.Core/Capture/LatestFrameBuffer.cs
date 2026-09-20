namespace ReTran.Core.Capture;

/// <summary>
/// Thread-safe latest-frame slot: Push replaces the waiting frame (old one dropped + disposed).
/// Khe frame-mới-nhất an toàn luồng: Push thay frame đang chờ (frame cũ bị drop + dispose).
/// </summary>
public sealed class LatestFrameBuffer : IDisposable
{
    private readonly object _lock = new();
    private Frame? _waiting;
    private bool _disposed;

    /// <summary>Number of frames dropped because nobody took them. Số frame bị drop vì không ai lấy.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Stores a frame, dropping the previous waiting frame if any. Lưu frame mới, drop + dispose frame đang chờ trước đó (nếu có).</summary>
    public void Push(Frame frame)
    {
        lock (_lock)
        {
            if (_disposed) { frame.Dispose(); return; }
            if (_waiting is not null) { _waiting.Dispose(); DroppedCount++; }
            _waiting = frame;
        }
    }

    /// <summary>Takes the latest waiting frame (caller owns it); false when empty. Lấy frame mới nhất đang chờ (người gọi nhận quyền sở hữu); trả về false khi rỗng.</summary>
    public bool TryTakeLatest(out Frame? frame)
    {
        lock (_lock) { frame = _waiting; _waiting = null; return frame is not null; }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; _waiting?.Dispose(); _waiting = null; }
    }
}
