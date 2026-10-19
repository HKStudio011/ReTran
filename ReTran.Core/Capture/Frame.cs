namespace ReTran.Core.Capture;

/// <summary>
/// A single captured frame in BGRA pixel order, owning its pixel array; the array may come from an
/// <see cref="IPixelPool"/> and is returned there on <see cref="Dispose"/> (no-pool frames just drop it).
/// Một khung hình đã chụp theo thứ tự điểm ảnh BGRA, sở hữu mảng điểm ảnh riêng; mảng có thể lấy từ
/// <see cref="IPixelPool"/> và sẽ được trả về pool khi <see cref="Dispose"/> (không pool thì bỏ mảng).
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

    private readonly IPixelPool? _pool;

    private Frame(int width, int height, byte[] pixels, IPixelPool? pool)
    { Width = width; Height = height; Pixels = pixels; _pool = pool; TimestampUtc = DateTime.UtcNow; }

    /// <summary>Creates a frame from a BGRA buffer (takes ownership of the array). Tạo khung hình từ buffer BGRA (sở hữu mảng đầu vào).</summary>
    /// <exception cref="ArgumentException">Thrown when buffer length != width*height*4.</exception>
    public static Frame FromBgra(int width, int height, byte[] pixels) => FromBgra(width, height, pixels, pool: null);

    /// <summary>
    /// Creates a frame from a BGRA buffer, taking ownership of the array; when <paramref name="pool"/>
    /// is given, <see cref="Dispose"/> returns the array to it (exactly once, idempotent). When
    /// <paramref name="pool"/> is <see langword="null"/>, the array is simply dropped on dispose.
    /// Tạo khung hình từ buffer BGRA, sở hữu mảng đầu vào; khi có <paramref name="pool"/>,
    /// <see cref="Dispose"/> trả mảng về pool (đúng một lần, idempotent). Khi <paramref name="pool"/>
    /// là <see langword="null"/>, mảng đơn giản bị bỏ khi dispose.
    /// </summary>
    /// <param name="width">Frame width in pixels, must be positive. Chiều rộng khung (pixel), phải dương.</param>
    /// <param name="height">Frame height in pixels, must be positive. Chiều cao khung (pixel), phải dương.</param>
    /// <param name="pixels">BGRA buffer of exactly width*height*4 bytes. Buffer BGRA đúng width*height*4 byte.</param>
    /// <param name="pool">Pool the buffer will be returned to on dispose, or <see langword="null"/>. Pool nhận trả buffer khi dispose, hoặc <see langword="null"/>.</param>
    /// <returns>The new frame owning <paramref name="pixels"/>. Khung hình mới sở hữu <paramref name="pixels"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when buffer length != width*height*4. Khi chiều dài buffer != width*height*4.</exception>
    public static Frame FromBgra(int width, int height, byte[] pixels, IPixelPool? pool)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Invalid dimensions.", nameof(width));
        if (pixels.Length != width * height * 4)
            throw new ArgumentException($"Expected {width * height * 4} bytes, got {pixels.Length}.", nameof(pixels));
        return new Frame(width, height, pixels, pool);
    }

    /// <summary>Returns true when at least <paramref name="threshold"/> fraction of pixels are near-black (smoke-test helper). Trả về true khi ít nhất threshold phần điểm ảnh gần đen (helper cho smoke test).</summary>
    public bool IsMostlyBlack(double threshold = 0.99)
    {
        long dark = 0, total = (long)Width * Height;
        for (int i = 0; i < Pixels.Length; i += 4)
            if (Pixels[i] < 16 && Pixels[i + 1] < 16 && Pixels[i + 2] < 16) dark++;
        return (double)dark / total >= threshold;
    }

    /// <summary>
    /// Releases the frame: returns the pixel buffer to its pool when one was given, exactly once
    /// (a second dispose is a no-op because the buffer is cleared). Trả khung hình: trả buffer
    /// điểm ảnh về pool (nếu có) đúng một lần (dispose lần hai là no-op vì buffer đã được xóa).
    /// </summary>
    public void Dispose()
    {
        if (Pixels.Length == 0) return;
        _pool?.Return(Pixels);
        Pixels = [];
    }
}
