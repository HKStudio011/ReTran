using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ReTran.Core.Capture;

/// <summary>
/// Encodes a <see cref="Frame"/> (BGRA) to a PNG file via System.Drawing.
/// Mã hóa một <see cref="Frame"/> (BGRA) thành tệp PNG qua System.Drawing.
/// </summary>
public static class PngWriter
{
    /// <summary>
    /// Writes <paramref name="frame"/> to <paramref name="path"/> as PNG (creates the parent directory when missing).
    /// Ghi <paramref name="frame"/> ra <paramref name="path"/> dưới dạng PNG (tự tạo thư mục cha khi chưa có).
    /// </summary>
    /// <param name="path">Destination file path. Đường dẫn tệp đích.</param>
    /// <param name="frame">Frame to encode (BGRA layout matches Format32bppArgb on little-endian Windows). Frame cần mã hóa.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> or <paramref name="frame"/> is null.</exception>
    public static void WriteBgra(string path, Frame frame)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(frame);

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        GCHandle handle = GCHandle.Alloc(frame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(
                frame.Width, frame.Height, frame.Stride,
                PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
            bitmap.Save(path, ImageFormat.Png);
        }
        finally
        {
            handle.Free();
        }
    }
}
