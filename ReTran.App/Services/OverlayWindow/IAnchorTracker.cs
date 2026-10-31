using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Resolves the on-screen rectangle of the current capture source (window bounds or monitor
/// bounds), so the overlay can cover exactly the area being translated.
/// Tìm hình chữ nhật màn hình của nguồn capture hiện tại (khổ cửa sổ hoặc khổ monitor),
/// để overlay phủ đúng vùng đang được dịch.
/// </summary>
public interface IAnchorTracker
{
    /// <summary>
    /// Returns the source's rectangle in physical pixels, or <see langword="null"/> when the
    /// source is gone or minimized. Trả về hình chữ nhật của nguồn theo pixel vật lý, hoặc
    /// <see langword="null"/> khi nguồn biến mất hoặc bị thu nhỏ.
    /// </summary>
    /// <param name="source">The capture source to locate. Nguồn capture cần xác định vị trí.</param>
    (int X, int Y, int Width, int Height)? GetAnchorRect(SourceSelection source);

    /// <summary>
    /// Whether the source should stay visible for the given foreground window. A window source
    /// stays visible when its own app is focused or when the focused app sits on another monitor.
    /// A screen source stays visible while other apps are focused and hides only when the ReTran
    /// controller itself is focused on the same monitor.
    /// Nguồn có nên tiếp tục hiện với cửa sổ foreground cho trước hay không. Nguồn cửa sổ hiện khi
    /// chính ứng dụng của nó đang focus hoặc khi ứng dụng đang focus nằm ở monitor khác. Nguồn màn
    /// hình vẫn hiện khi app khác focus và chỉ ẩn khi chính cửa sổ ReTran được focus trên cùng monitor.
    /// </summary>
    /// <param name="source">The capture source to test. Nguồn capture cần kiểm tra.</param>
    /// <param name="foregroundWindow">
    /// Window becoming foreground, as reported by a watcher; <see langword="null"/> asks the OS.
    /// Cửa sổ đang trở thành foreground do watcher báo; <see langword="null"/> sẽ hỏi hệ điều hành.
    /// </param>
    /// <returns>True when the overlay should stay visible. True khi overlay nên tiếp tục hiện.</returns>
    bool IsSourceForeground(SourceSelection source, nint? foregroundWindow = null);
}
