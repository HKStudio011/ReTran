namespace ReTran.Core.Capture;

using System.Collections.Concurrent;

/// <summary>Reusable BGRA buffers. Bộ đệm BGRA tái sử dụng.</summary>
public interface IPixelPool
{
    /// <summary>Rents a buffer of exactly <paramref name="length"/> bytes (ownership transfers to the caller).
    /// Cho thuê buffer đúng <paramref name="length"/> byte (quyền sở hữu chuyển cho người gọi).</summary>
    byte[] Rent(int length);

    /// <summary>Returns a rented buffer to the pool; null and empty buffers are ignored.
    /// Trả buffer đã thuê về pool; buffer null hoặc rỗng bị bỏ qua.</summary>
    void Return(byte[]? buffer);
}

/// <summary>
/// Thread-safe pool of BGRA byte arrays backed by a <see cref="ConcurrentBag{T}"/>: <see cref="BgraPool.Rent"/>
/// hands out a previously returned array that matches the requested length exactly (wrong-size arrays
/// are discarded, never resized). Capture and annotate threads dispose frames on different threads,
/// so pooling must be lock-free. Buffers are used only inside the process; nothing persists.
/// Pool mảng byte BGRA an toàn luồng dùng <see cref="ConcurrentBag{T}"/>: <see cref="BgraPool.Rent"/> trả
/// lại mảng đã lưu nếu đúng kích thước yêu cầu (mảng sai cỡ bị bỏ, không tự resize). Luồng capture và
/// annotate dispose frame trên luồng khác nhau nên pool phải an toàn luồng. Buffer chỉ dùng trong tiến
/// trình; không lưu gì xuống đĩa.
/// </summary>
public sealed class BgraPool : IPixelPool
{
    private readonly ConcurrentBag<byte[]> _buffers = [];

    /// <summary>
    /// Rents a buffer of exactly <paramref name="length"/> bytes, reusing a stored array when one
    /// matches. Cho thuê buffer đúng <paramref name="length"/> byte, tái dùng mảng đã lưu khi khớp.
    /// </summary>
    /// <param name="length">Exact number of bytes required; must be positive. Số byte yêu cầu chính xác; phải dương.</param>
    /// <returns>An array with exactly <paramref name="length"/> bytes. Mảng với đúng <paramref name="length"/> byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="length"/> is not positive. Khi <paramref name="length"/> không dương.</exception>
    public byte[] Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        while (_buffers.TryTake(out byte[]? buffer))
        {
            if (buffer.Length == length) return buffer;
            // Wrong-size arrays are dropped to the GC, never resized.
            // Mảng sai cỡ bỏ cho GC, không tự resize.
        }
        return new byte[length];
    }

    /// <summary>
    /// Returns a buffer for reuse; null and zero-length buffers are ignored.
    /// Trả buffer về để tái sử dụng; buffer null hoặc rỗng bị bỏ qua.
    /// </summary>
    /// <param name="buffer">Previously rented buffer, or <see langword="null"/>. Buffer đã thuê trước đó, hoặc <see langword="null"/>.</param>
    public void Return(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0) return;
        _buffers.Add(buffer);
    }
}
