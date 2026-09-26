namespace ReTran.Core.Capture;

/// <summary>
/// Writes a <see cref="Frame"/> (BGRA) to a PNG file via <see cref="BgraPngEncoder"/>.
/// Ghi <see cref="Frame"/> (BGRA) ra tệp PNG qua <see cref="BgraPngEncoder"/>.
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

        File.WriteAllBytes(path, BgraPngEncoder.ToPngBytes(frame));
    }
}
