namespace ReTran.Core.Serve;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ReTran.Core.Capture;
using Frame = ReTran.Core.Capture.Frame;

/// <summary>
/// GDI+ implementation of <see cref="IFrameAnnotator"/>: downscale (max 1280 wide, never
/// upscale), draw green boxes + translations, encode JPEG quality 70.
/// Bản hiện GDI+ của <see cref="IFrameAnnotator"/>: thu nhỏ (tối đa 1280 rộng, không phóng to),
/// vẽ box xanh + bản dịch, mã hoá JPEG quality 70.
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

    /// <summary>
    /// Annotates one frame per the interface contract.
    /// Ghi chú một khung theo hợp đồng interface.
    /// </summary>
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown when <paramref name="frame"/> pixels do not match its size. Khi pixel <paramref name="frame"/> không khớp kích thước.</exception>
    public byte[] Annotate(Frame frame, AnnotSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(frame);

        float scale = Math.Min(1f, MaxWidth / (float)frame.Width);
        int outW = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int outH = Math.Max(1, (int)Math.Round(frame.Height * scale));

        using Bitmap source = CopyPixels(frame);
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
        return stream.ToArray();
    }

    /// <summary>Copies BGRA pixels into a 32bpp bitmap (handles any source stride equal to width*4). Sao chép pixel BGRA vào bitmap 32bpp (chiều stride = width*4).</summary>
    private static Bitmap CopyPixels(Frame frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = frame.Width * 4;
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(frame.Pixels, y * rowBytes,
                    IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>
    /// Draws every box (2 px green outline) with its translated/original text right below.
    /// Vẽ mọi box (viền xanh 2 px) kèm text dịch/nguyên bản ngay dưới box.
    /// </summary>
    private static void DrawBoxes(Graphics g, AnnotSnapshot snapshot, float scale, int outW, int outH)
    {
        using var pen = new Pen(BoxColor, 2f);
        float fontSize = Math.Max(12f, 0.02f * outW); // at least 12 px, ~2% of width. tối thiểu 12 px, ~2% chiều rộng
        using var font = new Font("Segoe UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        using var outlineBrush = new SolidBrush(Color.Black);

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

            // Two-pass outline (black offsets then white) for legibility over any background.
            // Vẽ viền đen (offset) rồi chữ trắng để đọc được trên mọi nền.
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0)
                        g.DrawString(label, font, outlineBrush, lx + dx, ly + dy);
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
