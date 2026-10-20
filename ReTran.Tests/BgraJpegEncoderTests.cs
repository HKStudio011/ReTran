using System.Drawing;
using System.Drawing.Imaging;
using ReTran.Core.Capture;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class BgraJpegEncoderTests
{
    [Fact]
    public void ToJpegBytes_HasJpegMarker_AndMatchesDimensions()
    {
        Frame frame = Frame.FromBgra(8, 4, new byte[8 * 4 * 4]);
        byte[] jpeg = BgraJpegEncoder.ToJpegBytes(frame);
        Assert.True(jpeg.Length > 4);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpeg[..2]); // JPEG SOI marker
        using var ms = new MemoryStream(jpeg);
        using var bmp = new Bitmap(ms);
        Assert.Equal(8, bmp.Width);
        Assert.Equal(4, bmp.Height);
    }

    [Fact]
    public void ToJpegBytes_SameFrame_EncodesSmallerThanPng()
    {
        using Frame frame = PlasmaFrame();
        long png = BgraPngEncoder.ToPngBytes(frame).LongLength;
        long jpeg = BgraJpegEncoder.ToJpegBytes(frame).LongLength;
        Assert.True(jpeg < png, $"expected JPEG ({jpeg} B) smaller than PNG ({png} B) on the same frame");
    }

    [Fact]
    public void ToJpegBase64_DecodesToJpegBytes()
    {
        Frame frame = Frame.FromBgra(8, 4, new byte[8 * 4 * 4]);
        byte[] roundtrip = Convert.FromBase64String(BgraJpegEncoder.ToJpegBase64(frame));
        Assert.Equal(BgraJpegEncoder.ToJpegBytes(frame), roundtrip);
    }

    [Fact]
    public void ToJpegBytes_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BgraJpegEncoder.ToJpegBytes(null!));
    }

    /// <summary>
    /// Builds a plasma-like frame (sum of smooth sinusoids at incommensurate frequencies, the
    /// classic proxy for screenshot/photo content): PNG's predictor + deflate cannot crush it
    /// while JPEG's DCT represents it with a handful of coefficients — fully deterministic.
    /// Tạo khung dạng plasma (tổng các sóng sin trơn tần số không cộng thương, proxy kinh điển
    /// cho nội dung screenshot/ảnh): predictor + deflate của PNG không nén nổi, còn DCT của JPEG
    /// biểu diễn bằng vài hệ số — hoàn toàn deterministic.
    /// </summary>
    private static Frame PlasmaFrame()
    {
        const int w = 320, h = 160;
        byte[] pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double v = 128
                    + 40 * Math.Sin(x / 9.7) * Math.Cos(y / 7.3)
                    + 30 * Math.Sin((x + y) / 13.1)
                    + 25 * Math.Cos(x / 3.1 - y / 5.9);
                byte b = (byte)(int)Math.Round(v);
                int i = (y * w + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = b;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
        return Frame.FromBgra(w, h, pixels);
    }
}
