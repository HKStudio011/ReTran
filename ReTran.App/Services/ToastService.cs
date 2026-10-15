namespace ReTran_App.Services;

/// <summary>
/// Severity of a toast message — drives the toast stack's color styling.
/// Mức độ của thông điệp toast — quyết định màu hiển thị trên toast stack.
/// </summary>
public enum ToastSeverity
{
    /// <summary>Neutral information message. Thông điệp thông tin trung tính.</summary>
    Info,

    /// <summary>Error message. Thông điệp lỗi.</summary>
    Error,
}

/// <summary>
/// One toast message to display — identity is the <see cref="Id"/> so the UI can key and remove it.
/// Một thông điệp toast cần hiển thị — nhận diện bằng <see cref="Id"/> để UI key và loại bỏ đúng bản ghi.
/// </summary>
/// <param name="Message">The localized text to show. Chuỗi đã dịch cần hiển thị.</param>
/// <param name="Severity">The severity, defaulting to Info. Mức độ, mặc định Info.</param>
/// <param name="Id">Unique id of this toast instance. Id duy nhất của instance toast này.</param>
public record ToastItem(string Message, ToastSeverity Severity, Guid Id);

/// <summary>
/// Fire-and-forget toast bus: any service raises <see cref="Show"/> and the UI (MainLayout's toast
/// stack) renders the item from <see cref="ToastAdded"/> and auto-removes it after a few seconds.
/// Bus toast fire-and-forget: bất kỳ service nào gọi <see cref="Show"/> và UI (toast stack trong
/// MainLayout) render item từ <see cref="ToastAdded"/> rồi tự xoá sau vài giây.
/// </summary>
public sealed class ToastService
{
    /// <summary>Raised when <see cref="Show"/> is called — the UI adds the toast to its stack. Bắn khi <see cref="Show"/> được gọi — UI thêm toast vào stack.</summary>
    public event Action<ToastItem>? ToastAdded;

    /// <summary>
    /// Queues a toast for display. No-op when nothing is subscribed (e.g. during startup).
    /// Gửi một toast đi hiển thị. No-op khi chưa có subscriber (vd. lúc app mới khởi động).
    /// </summary>
    /// <param name="message">The text to display — already localized by the caller. Chuỗi cần hiển thị — caller đã dịch sẵn.</param>
    /// <param name="severity">Toast severity, defaulting to <see cref="ToastSeverity.Info"/>. Mức độ toast, mặc định <see cref="ToastSeverity.Info"/>.</param>
    public void Show(string message, ToastSeverity severity = ToastSeverity.Info) =>
        ToastAdded?.Invoke(new ToastItem(message, severity, Guid.NewGuid()));
}
