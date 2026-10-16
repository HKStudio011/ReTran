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
}
