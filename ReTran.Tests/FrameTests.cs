using ReTran.Core.Capture;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class FrameTests
{
    [Fact]
    public void FromBgra_SetsStride_ToWidthTimes4()
    {
        var f = Frame.FromBgra(320, 200, new byte[320 * 200 * 4]);
        Assert.Equal(320 * 4, f.Stride);
        Assert.Equal(320 * 200 * 4, f.ByteSize);
    }

    [Fact]
    public void FromBgra_Rejects_WrongLength()
    {
        Assert.Throws<ArgumentException>(() => Frame.FromBgra(10, 10, new byte[99]));
    }

    [Fact]
    public void IsMostlyBlack_Detects_Black_And_White()
    {
        var black = Frame.FromBgra(4, 4, new byte[4 * 4 * 4]);
        Assert.True(black.IsMostlyBlack());
        var white = Frame.FromBgra(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray());
        Assert.False(white.IsMostlyBlack());
    }
}
