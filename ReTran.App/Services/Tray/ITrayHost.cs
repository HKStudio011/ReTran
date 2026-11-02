namespace ReTran_App.Services.Tray;

/// <summary>
/// Severity icon for a tray balloon notification.
/// Icon mức độ cho thông báo balloon của tray.
/// </summary>
public enum BalloonIcon { None, Info, Warning, Error }

/// <summary>
/// Hosts the system-tray icon: creation, removal, context menu and balloon notifications.
/// Chủ hệ thống icon tray: tạo, xóa, menu ngữ cảnh và thông báo balloon.
/// </summary>
public interface ITrayHost : IDisposable
{
    /// <summary>
    /// Whether the notification-area icon is currently registered.
    /// Icon khu vực thông báo đang được đăng ký hay chưa.
    /// </summary>
    bool IsCreated { get; }

    /// <summary>
    /// Registers the tray icon with its tooltip and context-menu provider (idempotent).
    /// Đăng ký icon tray kèm tooltip và nguồn menu ngữ cảnh (idempotent).
    /// </summary>
    /// <param name="iconHandle">Native HICON to display; owned by the caller. HICON native để hiển thị; do người gọi sở hữu.</param>
    /// <param name="tooltip">Tooltip text shown on hover. Chữ tooltip hiện khi rê chuột.</param>
    /// <param name="menuProvider">Returns the current menu entries each time the context menu opens. Trả về các mục menu hiện tại mỗi lần mở menu ngữ cảnh.</param>
    void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider);

    /// <summary>
    /// Removes the tray icon and destroys the backing window (no-op when not created).
    /// Xóa icon tray và hủy cửa sổ nền (không làm gì nếu chưa tạo).
    /// </summary>
    void Remove();

    /// <summary>
    /// Shows a balloon notification; no-op when the icon is not registered.
    /// Hiện thông báo balloon; không làm gì nếu icon chưa đăng ký.
    /// </summary>
    /// <param name="title">Balloon title, truncated to 63 characters. Tiêu đề balloon, cắt tối đa 63 ký tự.</param>
    /// <param name="text">Balloon body, truncated to 255 characters. Nội dung balloon, cắt tối đa 255 ký tự.</param>
    /// <param name="icon">Severity icon for the balloon. Icon mức độ của balloon.</param>
    void ShowBalloon(string title, string text, BalloonIcon icon);

    /// <summary>
    /// Raised when the user picks a context-menu entry or left-clicks the icon (OpenApp).
    /// Phát khi người dùng chọn mục menu ngữ cảnh hoặc click chuột trái vào icon (OpenApp).
    /// </summary>
    event Action<TrayCommandId>? CommandSelected;
}
