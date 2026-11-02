namespace ReTran_App.Services.Tray;

/// <summary>
/// Identifies a command in the system-tray context menu.
/// Định danh một lệnh trong menu ngữ cảnh của tray.
/// </summary>
public enum TrayCommandId { OpenApp, ToggleOverlay, Exit }

/// <summary>
/// One entry of the system-tray context menu: its id, display label and checked state.
/// Một mục trong menu ngữ cảnh tray: id, nhãn hiển thị và trạng thái đánh dấu.
/// </summary>
/// <param name="Id">The command this entry triggers. Lệnh mà mục này kích hoạt.</param>
/// <param name="Label">Text shown in the menu; localized by the caller. Nhãn hiển thị trong menu; do người gọi dịch sẵn.</param>
/// <param name="IsChecked">Whether the entry is rendered checked; only the overlay toggle uses this. Có hiển thị dấu tick hay không; chỉ mục toggle overlay dùng giá trị này.</param>
public sealed record TrayCommand(TrayCommandId Id, string Label, bool IsChecked);

/// <summary>
/// Builds the system-tray context menu with a fixed command order.
/// Xây dựng menu ngữ cảnh tray với thứ tự lệnh cố định.
/// </summary>
public static class TrayMenuModel
{
    /// <summary>
    /// Builds the tray menu in the fixed order OpenApp, ToggleOverlay, Exit; only the overlay entry carries a check state.
    /// Thứ tự cố định: OpenApp, ToggleOverlay, Exit. Chỉ mục overlay mang trạng thái đánh dấu.
    /// </summary>
    /// <param name="openLabel">Label for the "open app" entry. Nhãn của mục mở ứng dụng.</param>
    /// <param name="overlayLabel">Label for the "toggle overlay" entry. Nhãn của mục bật/tắt overlay.</param>
    /// <param name="exitLabel">Label for the "exit" entry. Nhãn của mục thoát.</param>
    /// <param name="overlayOn">Whether the overlay is currently on; becomes the checked state of the toggle entry. Overlay đang bật hay không; thành trạng thái tick của mục toggle.</param>
    /// <returns>The ordered menu: OpenApp, ToggleOverlay, Exit. Menu có thứ tự: OpenApp, ToggleOverlay, Exit.</returns>
    public static IReadOnlyList<TrayCommand> Build(string openLabel, string overlayLabel, string exitLabel, bool overlayOn) => new[]
    {
        new TrayCommand(TrayCommandId.OpenApp, openLabel, false),
        new TrayCommand(TrayCommandId.ToggleOverlay, overlayLabel, overlayOn),
        new TrayCommand(TrayCommandId.Exit, exitLabel, false),
    };
}
