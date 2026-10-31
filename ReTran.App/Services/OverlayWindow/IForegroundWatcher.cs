namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Watches the system-wide foreground window and reports every change, so the local overlay can
/// hide/show immediately instead of waiting for the next anchor poll.
/// Theo dõi cửa sổ foreground toàn hệ thống và báo mỗi lần đổi, để overlay local ẩn/hiện ngay
/// thay vì chờ lần poll anchor kế tiếp.
/// </summary>
public interface IForegroundWatcher : IDisposable
{
    /// <summary>
    /// Starts delivering foreground changes to <paramref name="onForegroundChanged"/> (idempotent).
    /// Bắt đầu báo thay đổi foreground cho <paramref name="onForegroundChanged"/> (idempotent).
    /// </summary>
    /// <param name="onForegroundChanged">
    /// Called with the window that is becoming foreground; invoked from the watcher thread.
    /// Được gọi với cửa sổ đang trở thành foreground; chạy trên thread của watcher.
    /// </param>
    void Start(Action<nint> onForegroundChanged);
}
