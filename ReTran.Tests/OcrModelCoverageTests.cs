using ReTran_App.Core;
using ReTran_App.Services;
using Xunit;

public class OcrModelCoverageTests
{
    /// <summary>The v6 unified rec models' script coverage (Latin/CJK — no ko/ru/ar/...). Phạm vi chữ của model rec v6 hợp nhất (Latin/CJK — không có ko/ru/ar/...).</summary>
    private static OcrModelInfo V6Rec { get; } = new(
        "PP-OCRv6_small_rec", "rec",
        ["zh", "zh-hant", "en", "ja", "vi", "fr", "de", "es"],
        20.4, Installed: true, InUse: true);

    /// <summary>The korean v5 fallback rec (the recommended pair for src ko). Model rec v5 dự phòng tiếng Hàn (cặp khuyến nghị cho src ko).</summary>
    private static OcrModelInfo KoreanRec { get; } = new(
        "korean_PP-OCRv5_mobile_rec", "rec", ["ko", "en"], 14.0, Installed: true, InUse: false);

    [Fact]
    public void Covers_KoNotInV6Pair_IsFalse()
    {
        // Tình huống cảnh báo chính của spec §6.1: src ko trên cặp v6 phải cảnh báo.
        Assert.False(OcrModelCoverage.Covers(V6Rec, "ko"));
    }

    [Fact]
    public void Covers_ViInV6Pair_IsTrue()
    {
        Assert.True(OcrModelCoverage.Covers(V6Rec, "vi"));
    }

    [Fact]
    public void Covers_KoInKoreanFallback_IsTrue()
    {
        Assert.True(OcrModelCoverage.Covers(KoreanRec, "ko"));
    }

    [Fact]
    public void Covers_Auto_NeverWarns_EvenWhenSpecificLangsMissing()
    {
        // "auto" không ghim ngôn ngữ nào nên không bao giờ phát cảnh báo phủ.
        Assert.True(OcrModelCoverage.Covers(V6Rec, "auto"));
        Assert.True(OcrModelCoverage.Covers(KoreanRec, "auto"));
    }

    [Fact]
    public void Covers_NormalizesCaseAndWhitespace()
    {
        Assert.False(OcrModelCoverage.Covers(V6Rec, " KO "));
        Assert.True(OcrModelCoverage.Covers(KoreanRec, "KO"));
    }
}
