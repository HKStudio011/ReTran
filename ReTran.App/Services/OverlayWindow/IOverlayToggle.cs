namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// The overlay surface the floating menu needs: current state plus a toggle. Implemented by
/// <see cref="OverlayWindowService"/> so the menu can be tested without the native window machinery.
/// Bề mặt lớp phủ mà menu nổi cần: trạng thái hiện tại cộng công tắc bật/tắt. Do
/// <see cref="OverlayWindowService"/> implement để menu kiểm thử được mà không cần bộ máy cửa sổ native.
/// </summary>
public interface IOverlayToggle
{
    /// <summary>Whether the overlay is currently enabled. Lớp phủ đang bật hay không.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enables or disables the overlay (persisting either way).
    /// Bật hoặc tắt lớp phủ (lưu trong cả hai trường hợp).
    /// </summary>
    /// <param name="on">Desired state. Trạng thái mong muốn.</param>
    void SetEnabled(bool on);
}
