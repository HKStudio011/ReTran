namespace ReTran.Core.Serve;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReTran.Core.Capture;
using Frame = ReTran.Core.Capture.Frame;

/// <summary>
/// GDI+ implementation of <see cref="IFrameAnnotator"/>: downscales (max 1280 wide, never
/// upscale) by wrapping the captured BGRA buffer in place (pinned, no pixel copy), draws green
/// boxes with a dark pill behind each label (one DrawString per box — user-approved style),
/// encodes JPEG quality 70.
/// Bản hiện GDI+ của <see cref="IFrameAnnotator"/>: thu nhỏ (tối đa 1280 rộng, không phóng to)
/// bằng cách quấn buffer BGRA đã chụp tại chỗ (pin trong chỗ, không sao chép pixel), vẽ box
/// xanh với nền pill tối sau mỗi nhan đề (một lượt DrawString mỗi box — style đã duyệt), mã
/// hoá JPEG quality 70.
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

    /// <summary>Box outline color #22C55E. Màu viền box #22C55E.</summary>
    private static readonly Color BoxColor = Color.FromArgb(255, 34, 197, 94);

    /// <summary>Pill fill #B4000000 — dark translucent (user-approved 2026-10-19). Màu nền pill #B4000000 — đen mờ (user duyệt 2026-10-19).</summary>
    private static readonly Color PillColor = Color.FromArgb(180, 0, 0, 0);

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
    /// Draws every box (2 px green outline) with its translated/original text on a dark pill
    /// below the box: one MeasureString + FillRectangle + DrawString (replaces the two-pass
    /// outline drawing). Vẽ mọi box (viền xanh 2 px) kèm text dịch/nguyên bản trên nền pill
    /// tối dưới box: một lượt MeasureString + FillRectangle + DrawString (thay thế vẽ viền
    /// chữ hai lượt).
    /// </summary>
    private static void DrawBoxes(Graphics g, AnnotSnapshot snapshot, float scale, int outW, int outH)
    {
        using var pen = new Pen(BoxColor, 2f);
        float fontSize = Math.Max(12f, 0.02f * outW); // at least 12 px, ~2% of width. tối thiểu 12 px, ~2% chiều rộng
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var pillBrush = new SolidBrush(PillColor);

        foreach (AnnotBox box in snapshot.Boxes)
        {
            float x = (float)(box.X * snapshot.FrameWidth) * scale;
            float y = (float)(box.Y * snapshot.FrameHeight) * scale;
            float w = (float)(box.W * snapshot.FrameWidth) * scale;
            float h = (float)(box.H * snapshot.FrameHeight) * scale;
            g.DrawRectangle(pen, x, y, w, h);

            string label = string.IsNullOrEmpty(box.Translated) ? box.Text : box.Translated;
            if (string.IsNullOrEmpty(label)) continue;
            float lx = Math.Clamp(x, 0f, Math.Max(0f, outW - 1));
            float ly = Math.Clamp(y + h + 2f, 0f, Math.Max(0f, outH - fontSize - 2f));

            // Pill sized to the measured text (+4×2 px padding), clamped into the canvas.
            // Pill cỡ theo text đã đo (+4×2 px đệm), kẹp vào trong canvas.
            SizeF size = g.MeasureString(label, font);
            float pillX = Math.Clamp(lx - 2f, 0f, outW);
            float pillY = Math.Clamp(ly - 1f, 0f, outH);
            float pillW = Math.Min(size.Width + 4f, outW - pillX);
            float pillH = Math.Min(size.Height + 2f, outH - pillY);
            g.FillRectangle(pillBrush, pillX, pillY, pillW, pillH);
            g.DrawString(label, font, textBrush, lx, ly);
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
