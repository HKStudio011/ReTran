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
/// An ordered snapshot of the system-tray context menu, indexable by position or command id.
/// Ảnh chụp có thứ tự của menu ngữ cảnh tray, truy cập được theo vị trí hoặc theo id lệnh.
/// </summary>
public sealed class TrayMenu : IReadOnlyList<TrayCommand>
{
    private readonly List<TrayCommand> _commands;

    internal TrayMenu(IReadOnlyList<TrayCommand> commands) => _commands = new List<TrayCommand>(commands);

    /// <summary>
    /// Gets the menu entry with the given command id.
    /// Lấy mục menu có id lệnh cho trước.
    /// </summary>
    /// <param name="id">The command to look up. Id của lệnh cần tìm.</param>
    /// <returns>The matching menu entry. Mục menu khớp.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the menu does not contain the id. Ném khi menu không chứa id đó.</exception>
    public TrayCommand this[TrayCommandId id] =>
        _commands.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"Tray menu has no command {id}.");

    /// <summary>
    /// Gets the menu entry at the given position.
    /// Lấy mục menu tại vị trí cho trước.
    /// </summary>
    /// <param name="index">Zero-based position in the fixed order. Vị trí 0-based trong thứ tự cố định.</param>
    /// <returns>The menu entry at that position. Mục menu tại vị trí đó.</returns>
    public TrayCommand this[int index] => _commands[index];

    /// <summary>
    /// Gets the number of entries in the menu.
    /// Lấy số lượng mục trong menu.
    /// </summary>
    public int Count => _commands.Count;

    /// <summary>
    /// Enumerates the menu entries in their fixed order.
    /// Duyệt các mục menu theo thứ tự cố định.
    /// </summary>
    /// <returns>An enumerator over the entries. Bộ duyệt các mục.</returns>
    public IEnumerator<TrayCommand> GetEnumerator() => _commands.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _commands.GetEnumerator();
}

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
    /// <returns>The ordered menu, indexable by position or by <see cref="TrayCommandId"/>. Menu có thứ tự, truy cập theo vị trí hoặc theo id lệnh.</returns>
    public static TrayMenu Build(string openLabel, string overlayLabel, string exitLabel, bool overlayOn) => new(new[]
    {
        new TrayCommand(TrayCommandId.OpenApp, openLabel, false),
        new TrayCommand(TrayCommandId.ToggleOverlay, overlayLabel, overlayOn),
        new TrayCommand(TrayCommandId.Exit, exitLabel, false),
    });
}
