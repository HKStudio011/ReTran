using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// One overlay item in viewport pixel coordinates, ready to be drawn.
/// Một phần tử overlay trong tọa độ pixel viewport, sẵn sàng vẽ.
/// </summary>
/// <param name="X">Left edge in pixels. Cạnh trái theo pixel.</param>
/// <param name="Y">Top edge in pixels. Cạnh trên theo pixel.</param>
/// <param name="W">Width in pixels. Chiều rộng theo pixel.</param>
/// <param name="H">Height in pixels. Chiều cao theo pixel.</param>
/// <param name="Text">Text to draw (the translation). Văn bản cần vẽ (bản dịch).</param>
/// <param name="IsParagraph">True when the box is a merged multi-line paragraph block. Đúng khi hộp là khối đoạn văn gộp nhiều dòng.</param>
public sealed record DrawBox(double X, double Y, double W, double H, string Text, bool IsParagraph = false);

/// <summary>
/// Pure layout engine that turns an overlay frame into drawable boxes in viewport pixels.
/// Engine bố cục thuần biến khung overlay thành các hộp vẽ trong pixel viewport.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// Renders a frame into drawable boxes for the given viewport.
    /// Render khung hình thành các hộp vẽ cho viewport cho trước.
    /// </summary>
    /// <param name="frame">The overlay frame to render. Khung overlay cần render.</param>
    /// <param name="viewportWidth">Viewport width in pixels. Chiều rộng viewport theo pixel.</param>
    /// <param name="viewportHeight">Viewport height in pixels. Chiều cao viewport theo pixel.</param>
    /// <returns>Drawable boxes in viewport pixel coordinates, in frame order. Các hộp vẽ theo tọa độ pixel viewport, giữ thứ tự khung hình.</returns>
    public static IReadOnlyList<DrawBox> Render(OverlayFrame frame, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return RenderSubtitle(frame, viewportWidth, viewportHeight);
    }

    private static IReadOnlyList<DrawBox> RenderSubtitle(OverlayFrame frame, double vw, double vh)
    {
        (double contentX, double contentY, double contentW, double contentH) = ContentRect(frame, vw, vh);
        var result = new List<DrawBox>(frame.Boxes.Count);
        foreach (OverlayBox b in frame.Boxes)
        {
            if (string.IsNullOrWhiteSpace(b.Translated)) continue;

            double x = Math.Clamp(contentX + b.X * contentW, contentX, contentX + contentW);
            double y = Math.Clamp(contentY + b.Y * contentH, contentY, contentY + contentH);
            double w = Math.Min(b.W * contentW, contentX + contentW - x);
            double h = Math.Min(b.H * contentH, contentY + contentH - y);
            if (w <= 0 || h <= 0) continue;

            result.Add(new DrawBox(x, y, w, h, b.Translated, b.IsParagraph));
        }
        return result;
    }

    /// <summary>
    /// Fits the captured frame inside the viewport with its aspect preserved (letterbox/pillarbox).
    /// Khớp khung chụp vào viewport giữ nguyên tỉ lệ (thêm viền trên/dưới hoặc trái/phải).
    /// </summary>
    private static (double X, double Y, double W, double H) ContentRect(
        OverlayFrame frame, double vw, double vh)
    {
        if (frame.FrameWidth <= 0 || frame.FrameHeight <= 0)
            return (0, 0, vw, vh);

        double scale = Math.Min(vw / frame.FrameWidth, vh / frame.FrameHeight);
        double w = frame.FrameWidth * scale;
        double h = frame.FrameHeight * scale;
        return ((vw - w) / 2, (vh - h) / 2, w, h);
    }
}
