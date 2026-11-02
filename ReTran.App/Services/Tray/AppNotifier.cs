using Microsoft.Extensions.Logging;
using ReTran_App.Services;

namespace ReTran_App.Services.Tray;

/// <summary>
/// Chooses the notification channel for an app message: a tray balloon when the user is not looking at
/// the app, an in-app toast when they are.
/// Chọn kênh thông báo cho một thông điệp của app: balloon tray khi người dùng không nhìn vào app,
/// toast trong app khi họ đang nhìn.
/// </summary>
public static class NotifyRoute
{
    /// <summary>
    /// True when the message must leave the app through a tray bubble: the window is hidden, or it is
    /// visible but another window owns the foreground (e.g. the user is in a full-screen game).
    /// Đúng khi thông điệp phải ra khỏi app bằng bubble tray: cửa sổ đang ẩn, hoặc hiện nhưng cửa sổ
    /// khác đang giữ foreground (vd. người dùng đang chơi game full-screen).
    /// </summary>
    /// <param name="windowVisible">Whether the primary window is visible. Cửa sổ chính đang hiện hay ẩn.</param>
    /// <param name="windowIsForeground">Whether the primary window is the foreground window. Cửa sổ chính có đang là cửa sổ foreground hay không.</param>
    /// <returns>True to send a tray bubble, false to show an in-app toast. True để gửi bubble tray, false để hiển thị toast trong app.</returns>
    public static bool UseTrayBubble(bool windowVisible, bool windowIsForeground) =>
        !(windowVisible && windowIsForeground);
}

/// <summary>
/// Routes error notifications to the channel the user can actually see. A message is shown as an
/// in-app toast only while the window is visible AND in the foreground; otherwise it becomes a tray
/// balloon, because a toast raised while the app is hidden or behind a game is never seen.
/// Balloons are rate-limited by a cooldown: a burst of errors inside that window produces ONE balloon
/// (the first one) instead of a stack of popups fighting over the notification area. The cooldown is
/// charged only when a balloon actually fires — toasts are never rate-limited.
/// Định tuyến thông báo lỗi tới kênh mà người dùng thực sự nhìn thấy. Một thông điệp chỉ thành toast
/// trong app khi cửa sổ đang hiện VÀ đang foreground; ngược lại nó thành balloon tray, vì toast bắn
/// lúc app ẩn hoặc nằm sau game không bao giờ được thấy. Bubble có cửa sổ cooldown: chuỗi lỗi dồn
/// trong cửa sổ đó chỉ tạo MỘT bubble (cái đầu tiên) thay vì một chồng popup giành nhau khu vực thông
/// báo. Cửa sổ cooldown chỉ bị tính khi bubble thực sự bắn — toast không bao giờ bị giới hạn tần suất.
/// </summary>
public sealed class AppNotifier
{
    /// <summary>Balloon title — brand name, kept as a literal (not localized by ruling 2026-11-02). Tiêu đề balloon — tên thương hiệu, giữ nguyên chuỗi (không dịch theo quyết định 2026-11-02).</summary>
    private const string BalloonTitle = "ReTran";

    /// <summary>Default burst-collapse window when the caller does not supply one. Cửa sổ gộp chuỗi lỗi mặc định khi caller không truyền.</summary>
    private static readonly TimeSpan DefaultBubbleCooldown = TimeSpan.FromSeconds(3);

    private readonly ToastService _toast;
    private readonly ITrayHost _tray;
    private readonly Func<bool> _windowVisible;
    private readonly Func<bool> _windowIsForeground;
    private readonly ILogger<AppNotifier> _log;
    private readonly TimeSpan _bubbleCooldown;

    /// <summary>When the last balloon fired (Utc); <see cref="DateTime.MinValue"/> means "never". Thời điểm bubble gần nhất đã bắn (Utc); <see cref="DateTime.MinValue"/> nghĩa là "chưa bao giờ".</summary>
    private DateTime _lastBubbleUtc = DateTime.MinValue;

    /// <summary>
    /// Creates the notifier over the toast bus and the tray host, reading window state through the two
    /// predicates so the class stays free of WinUI/Win32 dependencies (and stays unit-testable).
    /// Tạo notifier trên bus toast và tray host, đọc trạng thái cửa sổ qua hai predicate để class
    /// không phụ thuộc WinUI/Win32 (và kiểm thử đơn vị được).
    /// </summary>
    /// <param name="toast">In-app toast bus. Bus toast trong app.</param>
    /// <param name="tray">Tray host used for balloons. Tray host dùng để bắn balloon.</param>
    /// <param name="windowVisible">Returns whether the primary window is visible. Trả về cửa sổ chính có đang hiện không.</param>
    /// <param name="windowIsForeground">Returns whether the primary window is the foreground window. Trả về cửa sổ chính có đang là foreground không.</param>
    /// <param name="log">Logger for suppressed balloons. Logger cho các bubble bị bỏ qua.</param>
    /// <param name="bubbleCooldown">
    /// Burst-collapse window for balloons; null selects 3 seconds. Keep short — Windows already serializes
    /// balloons and rejects a burst anyway. Cửa sổ gộp lỗi cho bubble; null chọn 3 giây. Giữ ngắn —
    /// Windows vốn đã tuần tự hoá balloon và vẫn từ chối khi bị bắn dồn.
    /// </param>
    public AppNotifier(ToastService toast, ITrayHost tray, Func<bool> windowVisible,
        Func<bool> windowIsForeground, ILogger<AppNotifier> log, TimeSpan? bubbleCooldown = null)
    {
        _toast = toast;
        _tray = tray;
        _windowVisible = windowVisible;
        _windowIsForeground = windowIsForeground;
        _log = log;
        _bubbleCooldown = bubbleCooldown ?? DefaultBubbleCooldown;
    }

    /// <summary>
    /// Publishes one message: an in-app toast when the user is looking at the app, otherwise a tray
    /// balloon (subject to the cooldown). Adds no try/catch of its own — <see cref="ITrayHost.ShowBalloon"/>
    /// is specified not to throw when the icon is not registered, so a notification cannot break the
    /// pipeline that reported the error.
    /// Đăng một thông điệp: toast trong app khi người dùng đang nhìn vào app, ngược lại là balloon tray
    /// (chịu cửa sổ cooldown). Không tự bọc try/catch — <see cref="ITrayHost.ShowBalloon"/> được cam kết
    /// không ném lỗi khi icon chưa đăng ký, nên việc thông báo không thể làm hỏng pipeline đã báo lỗi.
    /// </summary>
    /// <param name="message">The message text, already localized by the caller. Nội dung thông điệp, caller đã dịch sẵn.</param>
    /// <param name="severity">Severity for the toast and the balloon icon. Mức độ cho toast và icon của balloon.</param>
    public void Notify(string message, ToastSeverity severity = ToastSeverity.Info)
    {
        if (!NotifyRoute.UseTrayBubble(_windowVisible(), _windowIsForeground()))
        {
            _toast.Show(message, severity);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastBubbleUtc < _bubbleCooldown)
        {
            // Burst collapse: lỗi dồn trong cửa sổ cooldown chỉ phát bubble đầu tiên.
            _log.LogDebug("Tray balloon suppressed (within {Cooldown} of the previous one): {Message}",
                _bubbleCooldown, message);
            return;
        }

        _lastBubbleUtc = nowUtc;
        _tray.ShowBalloon(BalloonTitle, message, LevelFor(severity));
    }

    /// <summary>
    /// Maps a toast severity onto the balloon icon. ToastSeverity has no Warning member, so anything
    /// that is not an error gets the info icon; the mapping is exhaustive over the enum.
    /// Ánh xạ mức độ toast sang icon của balloon. ToastSeverity không có thành viên Warning, nên mọi
    /// mức độ khác lỗi đều lấy icon info; ánh xạ phủ toàn bộ enum.
    /// </summary>
    /// <param name="severity">The toast severity to map. Mức độ toast cần ánh xạ.</param>
    /// <returns>The matching balloon icon. Icon balloon tương ứng.</returns>
    internal static BalloonIcon LevelFor(ToastSeverity severity) => severity switch
    {
        ToastSeverity.Error => BalloonIcon.Error,
        _ => BalloonIcon.Info,
    };
}
