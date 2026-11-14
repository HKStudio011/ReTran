using System.Drawing;
using ReTran.Core.Serve;

namespace RetranCore.Tests;

public class BoxAnnotationStyleTests
{
    /// <summary>146-char Vietnamese translation shared by the sizing scenarios (measured 2026-11-12 on the old algorithm copy).
    /// Bản dịch 146 ký tự dùng chung cho các ca đo cỡ chữ (đo 2026-11-12 trên bản sao thuật toán cũ).</summary>
    private const string LongTranslation =
        "Đây là bản dịch tiếng Việt khá dài cho một đoạn hội thoại trong game, cần xuống dòng nhiều lần để hiển thị đầy đủ nội dung bên trong chiếc hộp này";

    private static BoxAnnotationStyle.TextFit Fit(string text, float innerWidth, float innerHeight,
        float boxHeight)
    {
        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using StringFormat format = BoxAnnotationStyle.CreateTextFormat();
        return BoxAnnotationStyle.Fit(g, text, innerWidth, innerHeight, boxHeight, format);
    }

    [Fact]
    public void Fit_LongTranslation_FitsInnerHeight()
    {
        // Mirrors the rendered box in FrameAnnotatorTests: 192×57.6 px → inner 188×49.6 px.
        // Khớp box được render trong FrameAnnotatorTests: 192×57,6 px → lòng 188×49,6 px.
        BoxAnnotationStyle.TextFit fit = Fit(
            "Đừng đến gần khu rừng này trước khi trăng lên.", 188f, 49.6f, 57.6f);

        Assert.True(fit.Fits,
            $"expected a size fitting the inner rect, got {fit.Size} px / {fit.Lines} lines");
        Assert.True(fit.Block.Height + 2f * BoxAnnotationStyle.PadY(fit.Size) <= 49.6f + 0.5f);
    }

    [Fact]
    public void Fit_TallBox_UsesVerticalRoom()
    {
        // Old algorithm (4-line cap, measured 2026-11-12): 27 px / 4 lines (147 of 322 px used).
        // New algorithm must pick a larger size and wrap further to use the box interior.
        // Thuật toán cũ (trần 4 dòng, đo 2026-11-12): 27px/4 dòng (dùng 147/322px). Thuật toán mới
        // phải chọn cỡ lớn hơn và wrap thêm để dùng lòng box. Biên độ lỏng để không gãy theo font máy khác.
        BoxAnnotationStyle.TextFit fit = Fit(LongTranslation, 356f, 202f, 210f);

        Assert.True(fit.Fits);
        Assert.True(fit.Size >= 22, $"expected >= 22 px, got {fit.Size}");
        Assert.True(fit.Lines >= 5, $"expected >= 5 lines, got {fit.Lines}");
        Assert.True(fit.Block.Height >= 0.8f * 202f,
            $"expected the block to use the box height (got {fit.Block.Height:0} of 202 px)");
    }

    [Fact]
    public void Fit_TinyBox_KeepsFloorAndReportsNotFitting()
    {
        const string text = "Đây là bản dịch tiếng Việt của một dòng hội thoại ngắn trong game";
        BoxAnnotationStyle.TextFit fit = Fit(text, 236f, 22f, 30f);

        Assert.False(fit.Fits);
        Assert.Equal((int)BoxAnnotationStyle.MinFontSize, fit.Size);
    }

    [Fact]
    public void Fit_WideShortBox_StaysSingleLine()
    {
        BoxAnnotationStyle.TextFit fit = Fit(LongTranslation, 1096f, 32f, 40f);

        Assert.True(fit.Fits);
        Assert.True(fit.Lines <= 2, $"expected at most two lines, got {fit.Lines}");
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
    public void TruncateToFit_CutsToLineBudget()
    {
        // TruncateToFit now serves only the viewport guard; it still cuts at a given size/line budget.
        // TruncateToFit giờ chỉ phục vụ guard viewport; vẫn cắt đúng tại cỡ/số dòng cho trước.
        const string text = "Một dòng bảng rất dài cần cắt bớt trước khi vẽ lên overlay của game";

        using var bitmap = new Bitmap(1, 1);
        using Graphics g = Graphics.FromImage(bitmap);
        using StringFormat format = BoxAnnotationStyle.CreateTextFormat();
        string truncated = BoxAnnotationStyle.TruncateToFit(g, text, 12, 220f, 20f, format, 1);

        Assert.EndsWith("…", truncated);
        Assert.True(truncated.Length < text.Length);
    }
}
