using ReTran_App.Core;
using ReTran_App.Services.OverlayWindow;

namespace ReTran.Tests;

public class OverlayLayoutTests
{
    static OverlayFrame Frame(params (double X, double Y, double W, double H, string Text, string Translated)[] boxes) =>
        new(1920, 1080, DateTime.UtcNow, boxes.Select(b => new OverlayBox(b.X, b.Y, b.W, b.H, b.Text, b.Translated)).ToList());

    [Fact]
    public void Subtitle_MapsRelativeCoordsToViewport()
    {
        OverlayFrame frame = Frame((0.1, 0.2, 0.3, 0.05, "HELLO", "Xin chào"));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, 1920, 1080);

        DrawBox box = Assert.Single(boxes);
        Assert.Equal(new DrawBox(192, 216, 576, 54, "Xin chào"), box);
    }

    [Fact]
    public void Subtitle_ClampsToViewportEdges()
    {
        OverlayFrame frame = Frame(
            (0.95, -0.1, 0.2, 0.3, "A", "B"),
            (1.0, 0.5, 0.1, 0.1, "C", "D"));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, 1000, 500);

        // 1920×1080 inside 1000×500 is pillarboxed (content 888.89 wide, 55.56 px bars).
        // 1920×1080 trong 1000×500 bị thêm viền trái/phải (nội dung rộng 888,89; viền 55,56).
        DrawBox box = Assert.Single(boxes);
        Assert.Equal(900.0, box.X, 3);
        Assert.Equal(0.0, box.Y, 3);
        Assert.Equal(44.444, box.W, 3);
        Assert.Equal(150.0, box.H, 3);
    }

    [Fact]
    public void Subtitle_LetterboxesFrameInsideViewport()
    {
        // 16:9 frame in a 1:1 viewport: the content must keep its aspect (pillarbox/letterbox)
        // instead of stretching the boxes with the viewport.
        // Frame 16:9 trong viewport 1:1: nội dung phải giữ tỉ lệ (thêm viền) thay vì kéo box
        // theo viewport.
        OverlayFrame frame = Frame((0.5, 0.5, 0.5, 0.5, "HELLO", "Xin chào"));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, 1000, 1000);

        DrawBox box = Assert.Single(boxes);
        Assert.Equal(500.0, box.X, 3);
        Assert.Equal(500.0, box.Y, 3);
        Assert.Equal(500.0, box.W, 3);
        Assert.Equal(281.25, box.H, 3);
    }

    [Fact]
    public void Subtitle_SkipsEmptyTranslation()
    {
        OverlayFrame frame = Frame(
            (0.1, 0.2, 0.3, 0.05, "HELLO", ""),
            (0.1, 0.4, 0.3, 0.05, "WORLD", "   "));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, 1920, 1080);

        Assert.Empty(boxes);
    }
}
