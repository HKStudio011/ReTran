using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Overlay display style: subtitles over the original text, or a text panel in a screen corner.
/// Kiển cách hiển thị overlay: phụ đề đè lên văn bản gốc, hoặc bảng chữ ở góc màn hình.
/// </summary>
public enum OverlayMode
{
    /// <summary>Translation drawn over each source text box. Bản dịch vẽ đè lên từng hộp văn bản gốc.</summary>
    Subtitle,

    /// <summary>Translation lines stacked in a corner panel. Các dòng bản dịch xếp trong bảng ở góc.</summary>
    Panel,
}

/// <summary>
/// Screen corner that anchors a <see cref="OverlayMode.Panel"/> layout.
/// Góc màn hình neo bố cục <see cref="OverlayMode.Panel"/>.
/// </summary>
public enum OverlayCorner
{
    /// <summary>Top-left corner. Góc trên trái.</summary>
    TopLeft,

    /// <summary>Top-right corner. Góc trên phải.</summary>
    TopRight,

    /// <summary>Bottom-left corner. Góc dưới trái.</summary>
    BottomLeft,

    /// <summary>Bottom-right corner. Góc dưới phải.</summary>
    BottomRight,
}

/// <summary>
/// View options fed into <see cref="OverlayLayout.Render"/>: mode, corner and font size multiplier.
/// Tùy chọn hiển thị đưa vào <see cref="OverlayLayout.Render"/>: chế độ, góc và hệ số cỡ chữ.
/// </summary>
/// <param name="Mode">Display mode. Chế độ hiển thị.</param>
/// <param name="Corner">Anchor corner for panel mode. Góc neo cho chế độ bảng.</param>
/// <param name="FontScale">Font size multiplier for panel lines. Hệ số cỡ chữ cho dòng bảng.</param>
public sealed record OverlayViewOptions(OverlayMode Mode, OverlayCorner Corner, double FontScale);

/// <summary>
/// One overlay item in viewport pixel coordinates, ready to be drawn.
/// Một phần tử overlay trong tọa độ pixel viewport, sẵn sàng vẽ.
/// </summary>
/// <param name="X">Left edge in pixels. Cạnh trái theo pixel.</param>
/// <param name="Y">Top edge in pixels. Cạnh trên theo pixel.</param>
/// <param name="W">Width in pixels. Chiều rộng theo pixel.</param>
/// <param name="H">Height in pixels. Chiều cao theo pixel.</param>
/// <param name="Text">Text to draw (the translation). Văn bản cần vẽ (bản dịch).</param>
/// <param name="IsPanelLine"><see langword="true"/> when the box is a panel line rather than a subtitle. <see langword="true"/> khi box là dòng bảng thay vì phụ đề.</param>
public sealed record DrawBox(double X, double Y, double W, double H, string Text, bool IsPanelLine);

/// <summary>
/// Pure layout engine that turns an overlay frame into drawable boxes in viewport pixels.
/// Engine bố cục thuần biến khung overlay thành các hộp vẽ trong pixel viewport.
/// </summary>
public static class OverlayLayout
{
    /// <summary>Maximum lines shown in panel mode. Số dòng tối đa hiển thị ở chế độ bảng.</summary>
    public const int PanelMaxLines = 10;

    /// <summary>Margin between the panel and the viewport edge (pixels). Khoảng cách giữa bảng và mép viewport (pixel).</summary>
    public const double PanelMargin = 12.0;

    /// <summary>Vertical gap between panel lines (pixels). Khoảng cách dọc giữa các dòng bảng (pixel).</summary>
    public const double PanelLineGap = 8.0;

    /// <summary>Panel width as a fraction of the viewport width. Chiều rộng bảng theo tỉ lệ chiều rộng viewport.</summary>
    public const double PanelWidthRatio = 0.4;

    /// <summary>Panel line height in pixels before font scaling. Chiều cao dòng bảng theo pixel trước khi scale cỡ chữ.</summary>
    public const double PanelLineHeight = 24.0;

    /// <summary>
    /// Renders a frame into drawable boxes for the given viewport.
    /// Render khung hình thành các hộp vẽ cho viewport cho trước.
    /// </summary>
    /// <param name="frame">The overlay frame to render. Khung overlay cần render.</param>
    /// <param name="options">Display mode, corner and font scale. Chế độ, góc và hệ số cỡ chữ.</param>
    /// <param name="viewportWidth">Viewport width in pixels. Chiều rộng viewport theo pixel.</param>
    /// <param name="viewportHeight">Viewport height in pixels. Chiều cao viewport theo pixel.</param>
    /// <returns>Drawable boxes in viewport pixel coordinates, in frame order. Các hộp vẽ theo tọa độ pixel viewport, giữ thứ tự khung hình.</returns>
    public static IReadOnlyList<DrawBox> Render(OverlayFrame frame, OverlayViewOptions options, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        return options.Mode == OverlayMode.Panel
            ? RenderPanel(frame, options, viewportWidth, viewportHeight)
            : RenderSubtitle(frame, viewportWidth, viewportHeight);
    }

    private static IReadOnlyList<DrawBox> RenderSubtitle(OverlayFrame frame, double vw, double vh)
    {
        var result = new List<DrawBox>(frame.Boxes.Count);
        foreach (OverlayBox b in frame.Boxes)
        {
            if (string.IsNullOrWhiteSpace(b.Translated)) continue;

            double x = Math.Max(0, b.X * vw);
            double y = Math.Max(0, b.Y * vh);
            double w = Math.Min(b.W * vw, vw - x);
            double h = Math.Min(b.H * vh, vh - y);
            if (w <= 0 || h <= 0) continue;

            result.Add(new DrawBox(x, y, w, h, b.Translated, IsPanelLine: false));
        }
        return result;
    }

    private static IReadOnlyList<DrawBox> RenderPanel(OverlayFrame frame, OverlayViewOptions options, double vw, double vh)
    {
        var texts = new List<string>(PanelMaxLines);
        foreach (OverlayBox b in frame.Boxes)
        {
            if (string.IsNullOrWhiteSpace(b.Translated)) continue;
            if (texts.Count > 0 && string.Equals(texts[^1], b.Translated, StringComparison.OrdinalIgnoreCase)) continue;

            texts.Add(b.Translated);
            if (texts.Count == PanelMaxLines) break;
        }

        double panelW = vw * PanelWidthRatio;
        double lineH = PanelLineHeight * options.FontScale;
        bool right = options.Corner is OverlayCorner.TopRight or OverlayCorner.BottomRight;
        bool bottom = options.Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight;
        double x = right ? vw - PanelMargin - panelW : PanelMargin;

        var result = new List<DrawBox>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            double y = bottom
                ? (vh - PanelMargin) - (i + 1) * lineH - i * PanelLineGap
                : PanelMargin + i * (lineH + PanelLineGap);
            result.Add(new DrawBox(x, y, panelW, lineH, texts[i], IsPanelLine: true));
        }
        return result;
    }
}
