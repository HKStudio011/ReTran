using ReTran_App.Core;
using ReTran_App.Services.OverlayWindow;

namespace ReTran.Tests;

public class OverlayLayoutTests
{
    static OverlayFrame Frame(params (double X, double Y, double W, double H, string Text, string Translated)[] boxes) =>
        new(1920, 1080, DateTime.UtcNow, boxes.Select(b => new OverlayBox(b.X, b.Y, b.W, b.H, b.Text, b.Translated)).ToList());

    static OverlayViewOptions Subtitle(double scale = 1.0) => new(OverlayMode.Subtitle, OverlayCorner.BottomLeft, scale);

    [Fact]
    public void Subtitle_MapsRelativeCoordsToViewport()
    {
        OverlayFrame frame = Frame((0.1, 0.2, 0.3, 0.05, "HELLO", "Xin chào"));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, Subtitle(), 1920, 1080);

        DrawBox box = Assert.Single(boxes);
        Assert.Equal(new DrawBox(192, 216, 576, 54, "Xin chào", false), box);
    }

    [Fact]
    public void Subtitle_ClampsToViewportEdges()
    {
        OverlayFrame frame = Frame(
            (0.95, -0.1, 0.2, 0.3, "A", "B"),
            (1.0, 0.5, 0.1, 0.1, "C", "D"));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, Subtitle(), 1000, 500);

        DrawBox box = Assert.Single(boxes);
        Assert.Equal(new DrawBox(950, 0, 50, 150, "B", false), box);
    }

    [Fact]
    public void Subtitle_SkipsEmptyTranslation()
    {
        OverlayFrame frame = Frame(
            (0.1, 0.2, 0.3, 0.05, "HELLO", ""),
            (0.1, 0.4, 0.3, 0.05, "WORLD", "   "));

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, Subtitle(), 1920, 1080);

        Assert.Empty(boxes);
    }

    [Fact]
    public void Panel_StacksTopLeft_FromTopDown()
    {
        OverlayFrame frame = Frame(
            (0, 0, 0.1, 0.05, "A", "A"),
            (0, 0, 0.1, 0.05, "B", "B"),
            (0, 0, 0.1, 0.05, "C", "C"));
        var options = new OverlayViewOptions(OverlayMode.Panel, OverlayCorner.TopLeft, 1.0);

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, options, 1000, 500);

        Assert.Equal(3, boxes.Count);
        Assert.Equal(new DrawBox(12, 12, 400, 24, "A", true), boxes[0]);
        Assert.Equal(new DrawBox(12, 44, 400, 24, "B", true), boxes[1]);
        Assert.Equal(new DrawBox(12, 76, 400, 24, "C", true), boxes[2]);
    }

    [Fact]
    public void Panel_BottomRight_DocksToBottomEdge()
    {
        OverlayFrame frame = Frame(
            (0, 0, 0.1, 0.05, "L1", "line one"),
            (0, 0, 0.1, 0.05, "L2", "line two"));
        var options = new OverlayViewOptions(OverlayMode.Panel, OverlayCorner.BottomRight, 1.0);

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, options, 1000, 500);

        Assert.Equal(2, boxes.Count);
        Assert.Equal(588, boxes[0].X);
        Assert.Equal(400, boxes[0].W);
        Assert.Equal(24, boxes[0].H);
        Assert.Equal(488, boxes[0].Y + boxes[0].H);
        Assert.True(boxes[1].Y < boxes[0].Y);
    }

    [Fact]
    public void Panel_DedupsConsecutiveDuplicates()
    {
        OverlayFrame frame = Frame(
            (0, 0, 0.1, 0.05, "A", "A"),
            (0, 0, 0.1, 0.05, "A", "a"),
            (0, 0, 0.1, 0.05, "B", "B"));
        var options = new OverlayViewOptions(OverlayMode.Panel, OverlayCorner.TopLeft, 1.0);

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, options, 1000, 500);

        Assert.Equal(2, boxes.Count);
        Assert.Equal("A", boxes[0].Text);
        Assert.Equal("B", boxes[1].Text);
    }

    [Fact]
    public void Panel_CapsAtMaxLines()
    {
        OverlayFrame frame = Frame(
            Enumerable.Range(0, 15)
                .Select(i => ((double)0, 0.0, 0.1, 0.05, $"S{i}", $"t{i}"))
                .ToArray());
        var options = new OverlayViewOptions(OverlayMode.Panel, OverlayCorner.TopLeft, 1.0);

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, options, 1000, 500);

        Assert.Equal(OverlayLayout.PanelMaxLines, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.IsPanelLine));
    }

    [Fact]
    public void Panel_SkipsEmptyTranslations()
    {
        OverlayFrame frame = Frame(
            (0, 0, 0.1, 0.05, "A", "A"),
            (0, 0, 0.1, 0.05, "", ""),
            (0, 0, 0.1, 0.05, "B", "B"),
            (0, 0, 0.1, 0.05, "   ", "   "),
            (0, 0, 0.1, 0.05, "C", "C"));
        var options = new OverlayViewOptions(OverlayMode.Panel, OverlayCorner.TopLeft, 1.0);

        IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, options, 1000, 500);

        Assert.Equal(3, boxes.Count);
        Assert.Equal(new[] { "A", "B", "C" }, boxes.Select(b => b.Text).ToArray());
    }
}
