namespace ReTran.Core.Capture;

/// <summary>
/// A single captured frame in BGRA pixel order, owned array (no pooling in M1).
/// Một khung hình đã chụp theo thứ tự điểm ảnh BGRA, mảng sở hữu riêng (M1 chưa dùng pool).
/// </summary>
public sealed class Frame : IDisposable
{
    /// <summary>Frame width in pixels. Chiều rộng khung hình (pixel).</summary>
    public int Width { get; }
    /// <summary>Frame height in pixels. Chiều cao khung hình (pixel).</summary>
    public int Height { get; }
    /// <summary>Bytes per row (Width * 4). Số byte mỗi hàng (Width * 4).</summary>
    public int Stride => Width * 4;
    /// <summary>Capture timestamp (UTC). Thời điểm chụp (UTC).</summary>
    public DateTime TimestampUtc { get; }
    /// <summary>BGRA pixel bytes, length = Height * Stride. Byte điểm ảnh BGRA.</summary>
    public byte[] Pixels { get; private set; }
    /// <summary>Total byte size (Height * Stride). Tổng số byte.</summary>
    public long ByteSize => (long)Height * Stride;

    private Frame(int width, int height, byte[] pixels)
    { Width = width; Height = height; Pixels = pixels; TimestampUtc = DateTime.UtcNow; }

    /// <summary>Creates a frame from a BGRA buffer (takes ownership of the array). Tạo khung hình từ buffer BGRA (sở hữu mảng đầu vào).</summary>
    /// <exception cref="ArgumentException">Thrown when buffer length != width*height*4.</exception>
    public static Frame FromBgra(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Invalid dimensions.", nameof(width));
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes, got {pixels.Length}.", nameof(pixels));
        return new Frame(width, height, pixels);
    }

    /// <summary>Returns true when at least <paramref name="threshold"/> fraction of pixels are near-black (smoke-test helper). Trả về true khi ít nhất threshold phần điểm ảnh gần đen (helper cho smoke test).</summary>
    public bool IsMostlyBlack(double threshold = 0.99)
    {
        long dark = 0, total = (long)Width * Height;
        for (int i = 0; i < Pixels.Length; i += 4)
            if (Pixels[i] < 16 && Pixels[i + 1] < 16 && Pixels[i + 2] < 16) dark++;
        return (double)dark / total >= threshold;
    }

    public void Dispose() { Pixels = []; }
}
