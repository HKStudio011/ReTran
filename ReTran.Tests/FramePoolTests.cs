using ReTran.Core.Capture;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class FramePoolTests
{
    /// <summary>Records returned bytes so tests can verify pool handoff. Ghi byte trả về để test kiểm tra chuyển giao pool.</summary>
    private sealed class RecordingPool : IPixelPool
    {
        public List<byte[]> Returned { get; } = [];
        public byte[] Rent(int length) => new byte[length];
        public void Return(byte[]? buffer)
        {
            if (buffer is not null && buffer.Length > 0) Returned.Add(buffer);
        }
    }

    [Fact]
    public void Dispose_WithPool_ReturnsExactBufferToPool()
    {
        var pool = new RecordingPool();
        byte[] pixels = new byte[16 * 16 * 4];
        var frame = Frame.FromBgra(16, 16, pixels, pool);
        frame.Dispose();
        Assert.Same(pixels, Assert.Single(pool.Returned));
    }

    [Fact]
    public void Dispose_TwiceWithPool_ReturnsBufferOnce()
    {
        var pool = new RecordingPool();
        byte[] pixels = new byte[16 * 16 * 4];
        var frame = Frame.FromBgra(16, 16, pixels, pool);
        frame.Dispose();
        frame.Dispose();
        Assert.Single(pool.Returned);
    }

    [Fact]
    public void Dispose_TwiceWithoutPool_KeepsOldBehavior()
    {
        byte[] pixels = new byte[16 * 16 * 4];
        var frame = Frame.FromBgra(16, 16, pixels);
        frame.Dispose();
        frame.Dispose();
        Assert.Empty(frame.Pixels);
    }

    [Fact]
    public void FromBgra_WithPool_RejectsWrongLengthWithoutReturning()
    {
        var pool = new RecordingPool();
        Assert.Throws<ArgumentException>(() => Frame.FromBgra(4, 4, new byte[99], pool));
        Assert.Empty(pool.Returned);
    }

    [Fact]
    public void Dispose_ThenRentNextFrame_RoundsTripThroughRealPool()
    {
        var pool = new BgraPool();
        byte[] pixels = new byte[16 * 16 * 4];
        var frame = Frame.FromBgra(16, 16, pixels, pool);
        frame.Dispose();
        Assert.Same(pixels, pool.Rent(16 * 16 * 4));
    }

    [Fact]
    public void Dispose_WithoutPool_LeavesPoolUntouched()
    {
        var pool = new BgraPool();
        byte[] pixels = new byte[16 * 16 * 4];
        var frame = Frame.FromBgra(16, 16, pixels);
        frame.Dispose();
        byte[] next = pool.Rent(16 * 16 * 4);
        Assert.NotSame(pixels, next);
        Assert.Empty(frame.Pixels);
    }
}
