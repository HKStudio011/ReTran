using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ReTran.Core.Capture;

/// <summary>
/// Encodes a <see cref="Frame"/> (BGRA) to in-memory PNG bytes via System.Drawing.
/// Mã hóa một <see cref="Frame"/> (BGRA) thành byte PNG trong bộ nhớ qua System.Drawing.
/// </summary>
public static class BgraPngEncoder
{
    /// <summary>
    /// Encodes <paramref name="frame"/> to PNG bytes without touching the file system.
    /// Mã hóa <paramref name="frame"/> thành byte PNG, không ghi tệp.
    /// </summary>
    /// <param name="frame">Frame to encode (BGRA, Stride = Width * 4). Frame cần mã hóa.</param>
    /// <returns>The PNG file bytes. Byte tệp PNG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static byte[] ToPngBytes(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        GCHandle handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                frame.Width, frame.Height, frame.Stride,
                PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Encodes <paramref name="frame"/> to a base64 PNG string (the ocr.spot transport form).
    /// Mã hóa <paramref name="frame"/> thành chuỗi base64 PNG (dạng gửi ocr.spot).
    /// </summary>
    /// <param name="frame">Frame to encode. Frame cần mã hóa.</param>
    /// <returns>Base64 PNG text. Chuỗi base64 PNG.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null.</exception>
    public static string ToPngBase64(Frame frame) => Convert.ToBase64String(ToPngBytes(frame));
}
