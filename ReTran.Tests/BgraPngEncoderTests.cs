using System.Drawing;
using System.Text;
using ReTran.Core.Capture;
using Xunit;
using Color = System.Drawing.Color;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class BgraPngEncoderTests
{
    [Fact]
    public void ToPngBytes_HasPngSignature_AndMatchesDimensions()
    {
        Frame frame = Frame.FromBgra(2, 2, new byte[2 * 2 * 4]);
        byte[] png = BgraPngEncoder.ToPngBytes(frame);
        Assert.True(png.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
        using var ms = new MemoryStream(png);
        using var bmp = new Bitmap(ms);
        Assert.Equal(2, bmp.Width);
        Assert.Equal(2, bmp.Height);
    }

    [Fact]
    public void ToPngBytes_RoundtripsPixelColor()
    {
        // BGRA memory layout == Format32bppArgb little-endian (B,G,R,A bytes).
        byte[] pixels = new byte[4];
        pixels[0] = 10; // B
        pixels[1] = 20; // G
        pixels[2] = 30; // R
        pixels[3] = 255; // A
        Frame frame = Frame.FromBgra(1, 1, pixels);

        using var ms = new MemoryStream(BgraPngEncoder.ToPngBytes(frame));
        using var bmp = new Bitmap(ms);
        Color c = bmp.GetPixel(0, 0);
        Assert.Equal(255, c.A);
        Assert.Equal(30, c.R);
        Assert.Equal(20, c.G);
        Assert.Equal(10, c.B);
    }

    [Fact]
    public void ToPngBase64_DecodesToPngBytes()
    {
        Frame frame = Frame.FromBgra(1, 1, new byte[] { 1, 2, 3, 4 });
        byte[] roundtrip = Convert.FromBase64String(BgraPngEncoder.ToPngBase64(frame));
        Assert.Equal(BgraPngEncoder.ToPngBytes(frame), roundtrip);
    }

    [Fact]
    public void ToPngBytes_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BgraPngEncoder.ToPngBytes(null!));
    }
}
