namespace ReTran.Core.Serve;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReTran.Core.Capture;
using Frame = ReTran.Core.Capture.Frame;

/// <summary>
/// GDI+ implementation of <see cref="IFrameAnnotator"/>: downscales (max 1280 wide, never
/// upscale) by wrapping the captured BGRA buffer in place (pinned, no pixel copy), draws teal
/// corner ticks around each source box plus a dark pill holding the translated text (font size
/// scales with the box height), encodes JPEG quality 70.
/// Bản hiện GDI+ của <see cref="IFrameAnnotator"/>: thu nhỏ (tối đa 1280 rộng, không phóng to)
/// bằng cách quấn buffer BGRA đã chụp tại chỗ (pin trong chỗ, không sao chép pixel), vẽ tick
/// teal 4 góc quanh mỗi box gốc kèm nền pill tối chứa bản dịch (cỡ chữ scale theo chiều cao
/// box), mã hoá JPEG quality 70.
/// </summary>
/// <remarks>
/// Stateless and thread-safe — one instance may serve many sessions. Không trạng thái, an toàn
/// luồng — một instance dùng được cho nhiều phiên.
/// </remarks>
public sealed class FrameAnnotator : IFrameAnnotator
{
    /// <summary>Maximum output width; frames wider are downscaled keeping the aspect ratio. Chiều rộng tối đa; khung rộng hơn bị thu nhỏ giữ tỉ lệ.</summary>
    private const int MaxWidth = 1280;

    /// <summary>JPEG quality (spec: 70). Chất lượng JPEG (spec: 70).</summary>
    private const long JpegQuality = 70;

    /// <inheritdoc/>
    /// <remarks>
    /// The caller keeps frame ownership and must not dispose it (mutate <c>Pixels</c>) before
    /// this returns: the source Bitmap reads the pinned array until the downscaled image and
    /// JPEG are done. Bytes of <paramref name="frame"/> are never copied.
    /// Người gọi giữ quyền sở hữu khung và không được dispose (thay <c>Pixels</c>) trước khi
    /// hàm trả về: Bitmap nguồn đọc mảng pin trực tiếp cho tới khi ảnh thu nhỏ và JPEG hoàn
    /// tất. Byte của <paramref name="frame"/> không bao giờ bị sao chép.
    /// </remarks>
    public AnnotatedFrame Annotate(Frame frame, AnnotSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(frame);

        float scale = Math.Min(1f, MaxWidth / (float)frame.Width);
        int outW = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int outH = Math.Max(1, (int)Math.Round(frame.Height * scale));

        // Pin the BGRA array and wrap it — GDI+ reads it in place while DrawImage downscales;
        // the pin must outlive the source Bitmap (freed before the pin in the using order).
        // Pin mảng BGRA và quấn lại — GDI+ đọc tại chỗ khi DrawImage thu nhỏ; pin phải sống
        // lâu hơn Bitmap nguồn (được giải phóng trước pin theo thứ tự using).
        GCHandle pin = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var source = new Bitmap(frame.Width, frame.Height, frame.Stride,
                PixelFormat.Format32bppArgb, pin.AddrOfPinnedObject());
            using var dest = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dest))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, 0, 0, outW, outH);
                g.CompositingMode = CompositingMode.SourceOver;
                if (snapshot is not null)
                    DrawBoxes(g, snapshot, scale, outW, outH);
            }

            using var stream = new MemoryStream();
            dest.Save(stream, JpegEncoder(), JpegParameters());
            return new AnnotatedFrame(stream.ToArray(), outW, outH);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>
    /// Draws every box: teal corner ticks on the source box, then a dark rounded pill inside
    /// the box holding the translation (up to two wrapped lines, font size derived from the box
    /// height and shrunk until it fits). Vẽ mọi box: tick teal 4 góc trên box gốc, rồi pill bo
    /// góc tối bên trong box chứa bản dịch (tối đa hai dòng wrap, cỡ chữ suy ra từ chiều cao
    /// box và thu nhỏ dần đến khi vừa).
    /// </summary>
    private static void DrawBoxes(Graphics g, AnnotSnapshot snapshot, float scale, int outW,
        int outH)
    {
        float ui = BoxAnnotationStyle.DecorationScale(outW);
        using var tickPen = new Pen(BoxAnnotationStyle.TickColor, MathF.Max(1f, 1.6f * ui))
        {
            StartCap = LineCap.Square,
            EndCap = LineCap.Square,
        };
        using var pillBrush = new SolidBrush(BoxAnnotationStyle.PillFill);
        using var pillPen = new Pen(BoxAnnotationStyle.PillBorder, MathF.Max(1f, ui));
        using var textBrush = new SolidBrush(BoxAnnotationStyle.TextColor);
        using var format = BoxAnnotationStyle.CreateTextFormat();
        using var pillPath = new GraphicsPath();

        foreach (AnnotBox box in snapshot.Boxes)
        {
            float x = (float)(box.X * snapshot.FrameWidth) * scale;
            float y = (float)(box.Y * snapshot.FrameHeight) * scale;
            float w = (float)(box.W * snapshot.FrameWidth) * scale;
            float h = (float)(box.H * snapshot.FrameHeight) * scale;
            if (w < 1f || h < 1f) continue;

            BoxAnnotationStyle.DrawTicks(g, tickPen, new RectangleF(x, y, w, h), MathF.Max(3f, 7f * ui));

            string label = string.IsNullOrEmpty(box.Translated) ? box.Text : box.Translated;
            if (string.IsNullOrWhiteSpace(label)) continue;

            RectangleF inner = BoxAnnotationStyle.InnerRect(new RectangleF(x, y, w, h));
            float innerW = inner.Width, innerH = inner.Height;
            if (innerW < 2f || innerH < 2f) continue;

            BoxAnnotationStyle.TextFit fit = BoxAnnotationStyle.Fit(
                g, label, innerW, innerH, h, format, maxLines: 2);
            SizeF block = fit.Block;
            if (!fit.Fits)
            {
                label = BoxAnnotationStyle.TruncateToFit(g, label, fit.Size, innerW, innerH, format, maxLines: 2);
                block = BoxAnnotationStyle.MeasureBlock(g, label, fit.Size, innerW, format);
            }

            float padX = BoxAnnotationStyle.PadX(fit.Size);
            float padY = BoxAnnotationStyle.PadY(fit.Size);
            float pillW = innerW;
            float pillH = MathF.Max(innerH, block.Height + 2f * padY); // tiny-box fallback may overflow
            float pillX = Math.Clamp(inner.X, 0f, MathF.Max(0f, outW - pillW));
            float pillY = Math.Clamp(inner.Y, 0f, MathF.Max(0f, outH - pillH));

            BoxAnnotationStyle.BuildRoundedRect(
                pillPath, new RectangleF(pillX, pillY, pillW, pillH), 3f * ui);
            g.FillPath(pillBrush, pillPath);
            g.DrawPath(pillPen, pillPath);
            g.DrawString(label, BoxAnnotationStyle.GetFont(fit.Size), textBrush,
                new RectangleF(pillX + padX, pillY + padY,
                    MathF.Max(1f, pillW - 2f * padX + 1f),
                    MathF.Max(1f, pillH - 2f * padY + 1f)),
                format);
        }
    }

    /// <summary>JPEG codec (first match). Codec JPEG (match đầu tiên).</summary>
    private static ImageCodecInfo JpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Encoder parameters carrying the quality. Tham số encoder chứa quality.</summary>
    private static EncoderParameters JpegParameters()
    {
        var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        return parameters;
    }
}
