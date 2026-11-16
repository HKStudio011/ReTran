using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="OcrModelChoices"/>: tier model-name composition, the legacy-rec routing
/// mirror, v6 coverage, preselection from the installed-model set, the needed-download set, and
/// the pair the wizard will apply — the pure rules behind the "Chọn model OCR" and
/// "Ngôn ngữ bổ sung" wizard screens.
/// Test cho <see cref="OcrModelChoices"/>: ghép tên model theo tier, mirror routing rec legacy,
/// phủ v6, preselect theo model đã cài, tập cần tải, và cặp sẽ áp dụng — các luật thuần sau 2 màn
/// "Chọn model OCR" và "Ngôn ngữ bổ sung".
/// </summary>
public class OcrModelChoicesTests
{
    [Fact]
    public void TierModelNames_ComposeCorrectly()
    {
        Assert.Equal(new[] { "tiny", "small", "medium" }, OcrModelChoices.Tiers);

        foreach (string tier in new[] { "tiny", "small", "medium" })
        {
            Assert.Equal($"PP-OCRv6_{tier}_det", OcrModelChoices.TierDet(tier));
            Assert.Equal($"PP-OCRv6_{tier}_rec", OcrModelChoices.TierRec(tier));
            Assert.Equal(tier, OcrModelChoices.TierOfDet($"PP-OCRv6_{tier}_det"));
        }

        Assert.Null(OcrModelChoices.TierOfDet("PP-OCRv6_small_rec")); // rec không phải det
        Assert.Null(OcrModelChoices.TierOfDet("korean_PP-OCRv5_mobile_rec"));
        Assert.Null(OcrModelChoices.TierOfDet("garbage"));
        Assert.Null(OcrModelChoices.TierOfDet(null));
    }

    [Fact]
    public void LegacyRecFor_MapsTheEightLanguages()
    {
        Assert.Equal("korean_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("ko"));
        Assert.Equal("cyrillic_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("ru"));
        Assert.Equal("arabic_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("ar"));
        Assert.Equal("th_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("th"));
        Assert.Equal("ta_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("ta"));
        Assert.Equal("te_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("te"));
        Assert.Equal("devanagari_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("hi"));
        Assert.Equal("el_PP-OCRv5_mobile_rec", OcrModelChoices.LegacyRecFor("el"));

        // Không rec legacy nào phủ → null (routing thật: v6 thắng hoặc không có model).
        Assert.Null(OcrModelChoices.LegacyRecFor("fr"));
        Assert.Null(OcrModelChoices.LegacyRecFor("xx"));
    }

    [Fact]
    public void IsV6Covered_AutoCoveredTrue_OthersFalse()
    {
        Assert.True(OcrModelChoices.IsV6Covered("auto"));
        Assert.True(OcrModelChoices.IsV6Covered("vi"));
        Assert.True(OcrModelChoices.IsV6Covered("en"));
        Assert.True(OcrModelChoices.IsV6Covered("ja"));
        Assert.True(OcrModelChoices.IsV6Covered("zh"));
        Assert.True(OcrModelChoices.IsV6Covered("VI")); // so khớp không phân biệt hoa thường

        Assert.False(OcrModelChoices.IsV6Covered("ko"));
        Assert.False(OcrModelChoices.IsV6Covered("ru"));
        Assert.False(OcrModelChoices.IsV6Covered("th"));
        Assert.False(OcrModelChoices.IsV6Covered("el"));
        Assert.False(OcrModelChoices.IsV6Covered("hi"));
        Assert.False(OcrModelChoices.IsV6Covered("ta"));
        Assert.False(OcrModelChoices.IsV6Covered("te"));
        Assert.False(OcrModelChoices.IsV6Covered("xx"));
    }

    [Fact]
    public void PreselectTiers_Full_TicksAndLocks()
    {
        string[] installed = ["PP-OCRv6_small_det", "PP-OCRv6_small_rec"];

        var p = OcrModelChoices.PreselectTiers(installed, "PP-OCRv6_small_det");

        Assert.Equal(new[] { "small" }, p.Ticked);
        Assert.Equal(new[] { "small" }, p.Locked); // đủ det+rec → tick + khoá
        Assert.Equal("small", p.ActiveTier);
    }

    [Fact]
    public void PreselectTiers_Partial_TicksUnlocked()
    {
        string[] installed = ["PP-OCRv6_small_det"]; // thiếu rec

        var p = OcrModelChoices.PreselectTiers(installed, null);

        Assert.Equal(new[] { "small" }, p.Ticked); // bán phần → tick nhưng tự do
        Assert.Empty(p.Locked);
        Assert.Equal("small", p.ActiveTier);
    }

    [Fact]
    public void PreselectTiers_Clean_DefaultsSmallFree()
    {
        var p = OcrModelChoices.PreselectTiers([], null);

        Assert.Equal(new[] { "small" }, p.Ticked);
        Assert.Empty(p.Locked);
        Assert.Equal("small", p.ActiveTier);
    }

    [Fact]
    public void PreselectTiers_ActiveFromLegacyPair()
    {
        // Settings: cặp det=tiny + rec legacy korean (rec không mang tier) → active theo det = tiny.
        string[] installed = ["PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec", "PP-OCRv6_small_det", "PP-OCRv6_small_rec"];

        var p = OcrModelChoices.PreselectTiers(installed, "PP-OCRv6_tiny_det");

        Assert.Equal(new[] { "tiny", "small" }, p.Ticked);
        Assert.Equal(new[] { "tiny", "small" }, p.Locked);
        Assert.Equal("tiny", p.ActiveTier);
    }

    [Fact]
    public void PreselectTiers_ActiveFallsToFirstTicked()
    {
        // Settings trỏ small nhưng small không còn trên đĩa → không tick → active = dòng tick đầu.
        string[] installed = ["PP-OCRv6_medium_det", "PP-OCRv6_medium_rec"];

        var p = OcrModelChoices.PreselectTiers(installed, "PP-OCRv6_small_det");

        Assert.Equal(new[] { "medium" }, p.Ticked);
        Assert.Equal("medium", p.ActiveTier);
    }

    [Fact]
    public void PreselectLegacyLanguages_InstalledOnly()
    {
        // Thứ tự hiển thị màn B = thứ tự LegacyLanguageCodes.
        Assert.Equal(
            new[] { "ko", "ru", "ar", "th", "ta", "te", "hi", "el" },
            OcrModelChoices.LegacyLanguageCodes);

        string[] installed = ["korean_PP-OCRv5_mobile_rec", "el_PP-OCRv5_mobile_rec", "PP-OCRv6_small_rec"];

        Assert.Equal(new[] { "ko", "el" }, OcrModelChoices.PreselectLegacyLanguages(installed));
    }

    [Fact]
    public void NeededModels_TiersPlusLegacyPlusExtras()
    {
        var needed = OcrModelChoices.NeededModels(["small", "medium"], "small", "ko", ["ru"]);

        Assert.Equal(
            new[]
            {
                "PP-OCRv6_small_det", "PP-OCRv6_small_rec",
                "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec",
                "korean_PP-OCRv5_mobile_rec",
                "cyrillic_PP-OCRv5_mobile_rec",
            },
            needed);
    }

    [Fact]
    public void NeededModels_AutoNoExtras_TierModelsOnly()
    {
        var needed = OcrModelChoices.NeededModels(["small"], "small", "auto", []);

        Assert.Equal(new[] { "PP-OCRv6_small_det", "PP-OCRv6_small_rec" }, needed);
    }

    [Fact]
    public void NeededModels_IncludesActiveTierModels_WhenTierListLacksIt()
    {
        // Phòng thủ: cặp đang chạy phải luôn được tải đủ dù answers lệch — active thêm vào cuối.
        var needed = OcrModelChoices.NeededModels(["small"], "medium", "auto", []);

        Assert.Equal(
            new[] { "PP-OCRv6_small_det", "PP-OCRv6_small_rec", "PP-OCRv6_medium_det", "PP-OCRv6_medium_rec" },
            needed);
    }

    [Fact]
    public void ExpectedPair_Auto_V6Pair()
    {
        var (det, rec) = OcrModelChoices.ExpectedPair("medium", "auto");

        Assert.Equal("PP-OCRv6_medium_det", det);
        Assert.Equal("PP-OCRv6_medium_rec", rec);
    }

    [Fact]
    public void ExpectedPair_Korean_TierDetPlusKorRec()
    {
        var (det, rec) = OcrModelChoices.ExpectedPair("tiny", "ko");

        Assert.Equal("PP-OCRv6_tiny_det", det);
        Assert.Equal("korean_PP-OCRv5_mobile_rec", rec);
    }

    [Fact]
    public void ScanInstalledModels_ReadsTempDirs()
    {
        string root = Path.Combine(Path.GetTempPath(), $"retran-models-{Guid.NewGuid():N}");
        try
        {
            string official = Path.Combine(root, "official_models");
            Directory.CreateDirectory(Path.Combine(official, "PP-OCRv6_small_det"));
            Directory.CreateDirectory(Path.Combine(official, "korean_PP-OCRv5_mobile_rec"));
            File.WriteAllText(Path.Combine(official, "stray.txt"), "x"); // file lạ — bỏ qua

            var found = OcrModelChoices.ScanInstalledModels(root);

            Assert.Equal(2, found.Count);
            Assert.Contains("PP-OCRv6_small_det", found);
            Assert.Contains("korean_PP-OCRv5_mobile_rec", found);

            // Thư mục cha không tồn tại → tập rỗng (không ném).
            Assert.Empty(OcrModelChoices.ScanInstalledModels(Path.Combine(root, "nope")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
