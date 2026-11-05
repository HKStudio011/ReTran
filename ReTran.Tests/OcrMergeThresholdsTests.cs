using ReTran.Core.Ocr;
using ReTran_App.Services;
using Xunit;

/// <summary>
/// Pins the pure App-side merge-threshold helper (text parsing + value ranges) used by the OCR
/// settings sheet, and cross-checks its defaults and ranges against Core so neither side can
/// drift silently. The test project references both assemblies; the App itself does not.
/// Ghim helper thuần phía App (phân tích chuỗi + dải giá trị) mà sheet cài đặt OCR dùng, và đối
/// chiếu mặc định/dải với Core để không bên nào trôi số âm thầm. Test project tham chiếu cả hai
/// assembly; bản thân App thì không.
/// </summary>
public class OcrMergeThresholdsTests
{
    [Fact]
    public void TryParseRatio_CommaAndDot()
    {
        Assert.True(OcrMergeThresholds.TryParseRatio("0.6", out double dot));
        Assert.True(OcrMergeThresholds.TryParseRatio("0,6", out double comma));
        Assert.Equal(0.6, dot);
        Assert.Equal(0.6, comma);

        Assert.True(OcrMergeThresholds.TryParseRatio(" 1.35 ", out double spaced));
        Assert.Equal(1.35, spaced);

        Assert.False(OcrMergeThresholds.TryParseRatio("", out _));
        Assert.False(OcrMergeThresholds.TryParseRatio(null, out _));
        Assert.False(OcrMergeThresholds.TryParseRatio("abc", out _));
        // Số mũ bị chính bộ parse từ chối (không phải bị dải giá trị chặn lại), nên "1e5" không
        // bao giờ biến thành 100000 rồi lọt qua ô nhập.
        Assert.False(OcrMergeThresholds.TryParseRatio("1e5", out _));
    }

    [Theory]
    [InlineData(0.09, false)]
    [InlineData(0.1, true)]
    [InlineData(2.0, true)]
    [InlineData(2.01, false)]
    [InlineData(double.NaN, false)]
    public void Ranges_Bounds_Gap(double value, bool expected) =>
        Assert.Equal(expected, OcrMergeThresholds.InGapRange(value));

    [Theory]
    [InlineData(0.99, false)]
    [InlineData(1.0, true)]
    [InlineData(3.0, true)]
    [InlineData(3.01, false)]
    [InlineData(double.NaN, false)]
    public void Ranges_Bounds_Height(double value, bool expected) =>
        Assert.Equal(expected, OcrMergeThresholds.InHeightRange(value));

    [Theory]
    [InlineData(-0.01, false)]
    [InlineData(0.0, true)]
    [InlineData(1.0, true)]
    [InlineData(1.01, false)]
    [InlineData(double.NaN, false)]
    public void Ranges_Bounds_Overlap(double value, bool expected) =>
        Assert.Equal(expected, OcrMergeThresholds.InOverlapRange(value));

    [Fact]
    public void AllValid_AllThreeInRange_IsTrue()
    {
        Assert.True(OcrMergeThresholds.AllValid(0.1, 1.0, 0.0));
        Assert.True(OcrMergeThresholds.AllValid(2.0, 3.0, 1.0));
        Assert.True(OcrMergeThresholds.AllValid(
            OcrMergeThresholds.DefaultGap, OcrMergeThresholds.DefaultHeight, OcrMergeThresholds.DefaultOverlap));
    }

    [Fact]
    public void AllValid_AnyOneOutOfRange_IsFalse()
    {
        Assert.False(OcrMergeThresholds.AllValid(2.01, 1.35, 0.5));
        Assert.False(OcrMergeThresholds.AllValid(0.6, 0.99, 0.5));
        Assert.False(OcrMergeThresholds.AllValid(0.6, 1.35, 1.01));
    }

    [Fact]
    public void AppDefaults_MatchCoreConsts()
    {
        // Core's shipped defaults (MergeThresholds.Default) come from the LineGrouper constants;
        // asserting both keeps the App consts tied to Core even if Core renames the source.
        // Mặc định gốc của Core (MergeThresholds.Default) lấy từ hằng số LineGrouper; kiểm cả hai
        // để hằng số App bám vào Core kể cả khi Core đổi tên nguồn.
        Assert.Equal(OcrMergeThresholds.DefaultGap, LineGrouper.MaxGapRatio);
        Assert.Equal(OcrMergeThresholds.DefaultHeight, LineGrouper.MaxHeightRatio);
        Assert.Equal(OcrMergeThresholds.DefaultOverlap, LineGrouper.MinOverlapRatio);

        Assert.Equal(OcrMergeThresholds.DefaultGap, MergeThresholds.Default.GapRatio);
        Assert.Equal(OcrMergeThresholds.DefaultHeight, MergeThresholds.Default.HeightRatio);
        Assert.Equal(OcrMergeThresholds.DefaultOverlap, MergeThresholds.Default.MinOverlapRatio);
    }

    [Fact]
    public void AppRanges_MatchCoreBounds()
    {
        // Mỗi biên của Core phải nằm trong dải App chấp nhận, và ngay bên ngoài biên đó phải bị
        // từ chối: dải hai phía lệch nhau bất kỳ hướng nào cũng làm test này đỏ.
        Assert.True(OcrMergeThresholds.InGapRange(MergeThresholds.GapRatioLow));
        Assert.True(OcrMergeThresholds.InGapRange(MergeThresholds.GapRatioHigh));
        Assert.False(OcrMergeThresholds.InGapRange(MergeThresholds.GapRatioLow - 0.01));
        Assert.False(OcrMergeThresholds.InGapRange(MergeThresholds.GapRatioHigh + 0.01));

        Assert.True(OcrMergeThresholds.InHeightRange(MergeThresholds.HeightRatioLow));
        Assert.True(OcrMergeThresholds.InHeightRange(MergeThresholds.HeightRatioHigh));
        Assert.False(OcrMergeThresholds.InHeightRange(MergeThresholds.HeightRatioLow - 0.01));
        Assert.False(OcrMergeThresholds.InHeightRange(MergeThresholds.HeightRatioHigh + 0.01));

        Assert.True(OcrMergeThresholds.InOverlapRange(MergeThresholds.OverlapRatioLow));
        Assert.True(OcrMergeThresholds.InOverlapRange(MergeThresholds.OverlapRatioHigh));
        Assert.False(OcrMergeThresholds.InOverlapRange(MergeThresholds.OverlapRatioLow - 0.01));
        Assert.False(OcrMergeThresholds.InOverlapRange(MergeThresholds.OverlapRatioHigh + 0.01));
    }
}
