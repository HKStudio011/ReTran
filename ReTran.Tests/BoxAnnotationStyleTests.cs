using System.Drawing;
using ReTran.Core.Serve;

namespace RetranCore.Tests;

public class BoxAnnotationStyleTests
{
    private static BoxAnnotationStyle.TextFit Fit(string text, float innerWidth, float innerHeight,
        float boxHeight, int maxLines = 2, bool shrink = true)
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using StringFormat format = BoxAnnotationStyle.CreateTextFormat();
        return BoxAnnotationStyle.Fit(g, text, innerWidth, innerHeight, boxHeight, format, maxLines, shrink);
    }

    [Fact]
    public void Fit_LongTranslation_WrapsToTwoLines()
    {
        // Mirrors the rendered box in FrameAnnotatorTests: 192×57.6 px → inner 188×49.6 px.
        // Khớp box được render trong FrameAnnotatorTests: 192×57,6 px → lòng 188×49,6 px.
        BoxAnnotationStyle.TextFit fit = Fit(
            "Đừng đến gần khu rừng này trước khi trăng lên.", 188f, 49.6f, 57.6f);

        Assert.True(fit.Fits,
            $"expected a size fitting two lines, got {fit.Size} px / {fit.Lines} lines");
        Assert.Equal(2, fit.Lines);
        Assert.True(fit.Block.Height + 2f * BoxAnnotationStyle.PadY(fit.Size) <= 49.6f + 0.5f);
    }

    [Fact]
    public void Fit_FontSizeGrowsWithBoxHeight()
    {
        BoxAnnotationStyle.TextFit shortBox = Fit("Chào", 188f, 22f, 30f);
        BoxAnnotationStyle.TextFit tallBox = Fit("Chào", 188f, 82f, 90f);

        Assert.True(shortBox.Fits);
        Assert.True(tallBox.Fits);
        Assert.True(tallBox.Size > shortBox.Size + 10,
            $"tall box font {tallBox.Size} px should clearly exceed short box font {shortBox.Size} px");
    }

    [Fact]
    public void BaseFontSize_ScalesWithBoxHeightAndClamps()
    {
        Assert.Equal(12, BoxAnnotationStyle.BaseFontSize(18f));  // below the 12 px floor
        Assert.Equal(19, BoxAnnotationStyle.BaseFontSize(38f));  // mockup: 38 px box → ~19 px
        Assert.Equal(40, BoxAnnotationStyle.BaseFontSize(200f)); // above the 40 px cap
    }

    [Fact]
    public void Fit_WithoutShrink_KeepsBaseSizeAndTruncates()
    {
        const string text = "Một dòng bảng rất dài cần cắt bớt trước khi vẽ lên overlay của game";
        BoxAnnotationStyle.TextFit fit = Fit(text, 220f, 20f, 24f, maxLines: 1, shrink: false);
        Assert.Equal(BoxAnnotationStyle.BaseFontSize(24f), fit.Size);

        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using StringFormat format = BoxAnnotationStyle.CreateTextFormat();
        string truncated = BoxAnnotationStyle.TruncateToFit(g, text, fit.Size, 220f, 20f, format, 1);

        Assert.EndsWith("…", truncated);
        Assert.True(truncated.Length < text.Length);
    }
}
