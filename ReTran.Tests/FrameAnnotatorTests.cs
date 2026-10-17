using System.Drawing;
using ReTran.Core.Capture;
using ReTran.Core.Serve;
using Color = System.Drawing.Color;
using Frame = ReTran.Core.Capture.Frame;
using Image = System.Drawing.Image;

namespace RetranCore.Tests;

public class FrameAnnotatorTests
{
    private static Frame SolidFrame(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return Frame.FromBgra(width, height, pixels);
    }

    private static Bitmap Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        using Image image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    private static bool IsGreen(Color c) => c.G > 150 && c.R < 120 && c.B < 120;

    [Fact]
    public void Annotate_WithoutSnapshot_ReturnsJpegCappedAt1280()
    {
        using Frame frame = SolidFrame(2560, 1440, 10, 20, 30);
        byte[] jpeg = new FrameAnnotator().Annotate(frame, null);

        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(1280, decoded.Width);
        Assert.Equal(720, decoded.Height);
    }

    [Fact]
    public void Annotate_WithSnapshot_DrawsGreenBox()
    {
        using Frame frame = SolidFrame(640, 360, 0, 0, 0);
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.1, 0.3, 0.1, "Hello", "Xin chào")]);

        byte[] jpeg = new FrameAnnotator().Annotate(frame, snapshot);

        using Bitmap decoded = Decode(jpeg);
        int green = 0;
        for (int y = 30; y < 80; y++)
            for (int x = 58; x < 262; x++)
                if (IsGreen(decoded.GetPixel(x, y))) green++;
        Assert.True(green > 0, "expected green (#22C55E) pixels inside/around the box region");
    }

    [Fact]
    public void Annotate_RendersVietnameseAndSymbol_WithoutThrowing()
    {
        using Frame frame = SolidFrame(640, 360, 0, 0, 0);
        var snapshot = new AnnotSnapshot(640, 360,
            [new AnnotBox(0.1, 0.4, 0.5, 0.1, "Xin chào ∑ ẻ", "∑ ẻ chào")]);

        byte[] jpeg = new FrameAnnotator().Annotate(frame, snapshot);

        Assert.NotNull(jpeg);
        Assert.NotEmpty(jpeg);
        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(640, decoded.Width);
    }

    [Fact]
    public void AnnotatorFrameCopy_SurvivesSourceDispose()
    {
        using Frame source = SolidFrame(64, 36, 0, 0, 255);

        // The annotation handler clones the shared frame before OcrLoop disposes it
        // (see PipelineHost.StartAsync) — this lock-step contract must survive disposal.
        // Handler clone khung dùng chung trước khi OcrLoop dispose nó — hợp đồng này phải sống qua dispose.
        var copy = Frame.FromBgra(source.Width, source.Height, (byte[])source.Pixels.Clone());
        source.Dispose();

        byte[] jpeg = new FrameAnnotator().Annotate(copy, null);
        using Bitmap decoded = Decode(jpeg);
        Assert.Equal(64, decoded.Width);
        Assert.Equal(36, decoded.Height);
    }
}
