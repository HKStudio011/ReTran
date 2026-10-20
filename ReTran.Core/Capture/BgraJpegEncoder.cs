using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ReTran.Core.Capture;

/// <summary>
/// Encodes a <see cref="Frame"/> (BGRA) to in-memory JPEG bytes via System.Drawing.
/// Mã hóa một <see cref="Frame"/> (BGRA) thành byte JPEG trong bộ nhớ qua System.Drawing.
/// </summary>
public static class BgraJpegEncoder
{
    /// <summary>
    /// Default JPEG quality for the ocr.spot transport.
    /// Chất lượng JPEG mặc định cho đường truyền ocr.spot.
    /// </summary>
    public const long DefaultQuality = 85;

    /// <summary>JPEG codec (first match). Codec JPEG (match đầu tiên).</summary>
    private static readonly ImageCodecInfo JpegCodec =
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Encodes <paramref name="frame"/> to JPEG bytes without touching the file system.
    /// Mã hóa <paramref name="frame"/> thành byte JPEG, không ghi tệp.
    /// </summary>
    /// <param name="frame">Frame to encode (BGRA, Stride = Width * 4). Frame cần mã hóa.</param>
    /// <param name="quality">JPEG quality 1..100. Chất lượng JPEG 1..100.</param>
    /// <returns>The JPEG file bytes. Byte tệp JPEG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static byte[] ToJpegBytes(Frame frame, long quality = DefaultQuality)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GCHandle handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                frame.Width, frame.Height, frame.Stride,
                PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(ms, JpegCodec, parameters);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Encodes <paramref name="frame"/> to a base64 JPEG string (the ocr.spot transport form).
    /// Mã hóa <paramref name="frame"/> thành chuỗi base64 JPEG (dạng gửi ocr.spot).
    /// </summary>
    /// <param name="frame">Frame to encode. Frame cần mã hóa.</param>
    /// <returns>Base64 JPEG text. Chuỗi base64 JPEG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static string ToJpegBase64(Frame frame) => Convert.ToBase64String(ToJpegBytes(frame));
}
