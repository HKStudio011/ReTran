namespace ReTran_App.Services.Window;

/// <summary>
/// What the app should do when the primary window requests to close (X button, Alt+F4).
/// Quyết định app phải làm gì khi cửa sổ chính yêu cầu đóng (nút X, Alt+F4).
/// </summary>
public enum CloseDecision
{
    /// <summary>Cancel the close and hide the window to the tray. Hủy đóng và ẩn cửa sổ xuống khay hệ thống.</summary>
    HideToTray,

    /// <summary>Let the window close for real. Cho phép cửa sổ đóng thật.</summary>
    AllowClose,
}

/// <summary>
/// Pure decision table for window closes, kept free of WinUI types so it is unit-testable.
/// Bảng quyết định đóng cửa sổ thuần túy, tách khỏi type WinUI để unit test được.
/// </summary>
public static class ClosePolicy
{
    /// <summary>
    /// An in-progress update or a real app exit allows the close; everything else hides to the tray.
    /// Update đang chạy hoặc app đã chọn thoát thật → cho đóng; còn lại ẩn xuống tray.
    /// </summary>
    /// <param name="updateInProgress">Whether an update run is in progress. Có lượt update đang chạy.</param>
    /// <param name="appIsExiting">Whether the app began a real exit via <c>BeginExit</c>. App đã bắt đầu thoát thật qua <c>BeginExit</c>.</param>
    /// <returns><see cref="CloseDecision.AllowClose"/> or <see cref="CloseDecision.HideToTray"/>. <see cref="CloseDecision.AllowClose"/> hoặc <see cref="CloseDecision.HideToTray"/>.</returns>
    public static CloseDecision Decide(bool updateInProgress, bool appIsExiting) =>
        updateInProgress || appIsExiting ? CloseDecision.AllowClose : CloseDecision.HideToTray;
}

/// <summary>
/// Guard deciding which created windows get the Closing hook and tray start — only the primary
/// window (ordinal 0) is hooked; Plan 2 opens a second floating-menu window, and hooking it too
/// would create two tray icons and hide twice.
/// Guard quyết định window nào được hook Closing và start tray — chỉ window chính (ordinal 0)
/// được hook; Plan 2 mở thêm window menu nổi, hook luôn sẽ sinh 2 icon khay và ẩn hai lần.
/// </summary>
public static class WindowHooks
{
    /// <summary>
    /// Returns <see langword="true"/> only for the primary window (ordinal 0).
    /// Chỉ trả <see langword="true"/> cho window chính (ordinal 0).
    /// </summary>
    /// <param name="windowOrdinal">Zero-based creation order of the window. Thứ tự tạo window, đánh số từ 0.</param>
    /// <returns>Whether the window should be hooked. Window có được hook hay không.</returns>
    public static bool ShouldHookWindow(int windowOrdinal) => windowOrdinal == 0;
}
