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

    // ── v2: derive từ catalog (17-11) ─────────────────────────────────────────────────────────────

    private static OcrModelInfo Model(string name, string kind, params string[] langs) =>
        new(name, kind, langs, 1f);

    private static IReadOnlyList<OcrModelInfo> RealPlus(params OcrModelInfo[] extra) =>
        [.. OcrCatalogDisplay.Models, .. extra];

    [Fact]
    public void DetModels_And_RecModels_FromCatalog()
    {
        Assert.Equal(
            new[] { "PP-OCRv6_tiny_det", "PP-OCRv6_small_det", "PP-OCRv6_medium_det" },
            OcrModelChoices.DetModels());
        Assert.Equal(
            new[] { "PP-OCRv6_tiny_rec", "PP-OCRv6_small_rec", "PP-OCRv6_medium_rec" },
            OcrModelChoices.RecModels());
    }

    [Fact]
    public void SizeToken_ParsesName_FallsBackRaw()
    {
        Assert.Equal("medium", OcrModelChoices.SizeToken("PP-OCRv6_medium_det"));
        Assert.Equal("big", OcrModelChoices.SizeToken("PP-OCRv6_big_rec"));
        Assert.Equal("weird", OcrModelChoices.SizeToken("weird"));
    }

    [Fact]
    public void SizeTokens_IncludesCatalogToken_And_BigAppears()
    {
        Assert.Equal(new[] { "tiny", "small", "medium" }, OcrModelChoices.SizeTokens());

        var withBig = RealPlus(
            Model("PP-OCRv6_big_det", "det", "vi"),
            Model("PP-OCRv6_big_rec", "rec", "vi"));
        Assert.Contains("big", OcrModelChoices.SizeTokens(withBig));
    }

    [Fact]
    public void LanguageChoices_EightToday()
    {
        var choices = OcrModelChoices.LanguageChoices();

        Assert.Equal(new[] { "ko", "ru", "ar", "th", "ta", "te", "hi", "el" },
            choices.Select(c => c.PrimaryLang));
        Assert.All(choices, c => Assert.Single(c.Variants));
        Assert.Equal("cyrillic_PP-OCRv5_mobile_rec", choices.Single(c => c.PrimaryLang == "ru").Variants[0]);
        // 2 rec "chết" không bao giờ xuất hiện: eslav bị nuốt bởi cyrillic, chinese_cht bị v6 phủ.
        Assert.DoesNotContain(choices, c => c.Variants.Any(v => v.Contains("eslav", StringComparison.Ordinal)));
        Assert.DoesNotContain(choices, c => c.Variants.Any(v => v.Contains("chinese_cht", StringComparison.Ordinal)));
    }

    [Fact]
    public void LanguageChoices_EslavAbsorbed_ServerSiblingShown()
    {
        var catalog = RealPlus(Model("korean_PP-OCRv5_server_rec", "rec", "ko", "en"));

        var choices = OcrModelChoices.LanguageChoices(catalog);

        Assert.Equal(8, choices.Count);
        LanguageChoice ko = choices.Single(c => c.PrimaryLang == "ko");
        Assert.Equal(new[] { "korean_PP-OCRv5_mobile_rec", "korean_PP-OCRv5_server_rec" }, ko.Variants);
    }

    [Fact]
    public void LanguageChoices_EnglishOnlyAndV6PrimaryEntry_Dropped()
    {
        var catalog = RealPlus(
            Model("en_only_PP-OCRv5_mobile_rec", "rec", "en"),
            Model("latin_PP-OCRv5_mobile_rec", "rec", "fr", "en"));

        var choices = OcrModelChoices.LanguageChoices(catalog);

        Assert.Equal(8, choices.Count);
        Assert.DoesNotContain(choices, c => c.Variants.Any(v =>
            v.StartsWith("en_only", StringComparison.Ordinal) || v.StartsWith("latin", StringComparison.Ordinal)));
    }

    [Fact]
    public void PreselectModels_InstalledTickedLocked_ActiveFromCurrent()
    {
        string[] installed = ["PP-OCRv6_tiny_det", "PP-OCRv6_small_det"];
        var p = OcrModelChoices.PreselectModels(installed, "PP-OCRv6_small_det", OcrModelChoices.DetModels());

        Assert.Equal(new[] { "PP-OCRv6_tiny_det", "PP-OCRv6_small_det" }, p.Ticked);
        Assert.Equal(new[] { "PP-OCRv6_tiny_det", "PP-OCRv6_small_det" }, p.Locked);
        Assert.Equal("PP-OCRv6_small_det", p.Active);
    }

    [Fact]
    public void PreselectModels_Clean_SmallFreeDefault()
    {
        var p = OcrModelChoices.PreselectModels([], null, OcrModelChoices.DetModels());

        Assert.Equal(new[] { "PP-OCRv6_small_det" }, p.Ticked);
        Assert.Empty(p.Locked);
        Assert.Equal("PP-OCRv6_small_det", p.Active);
    }

    [Fact]
    public void PreselectModels_CurrentNotTicked_FallsBackToFirstTicked()
    {
        string[] installed = ["PP-OCRv6_tiny_det"];
        var p = OcrModelChoices.PreselectModels(installed, "PP-OCRv6_medium_det", OcrModelChoices.DetModels());

        Assert.Equal(new[] { "PP-OCRv6_tiny_det" }, p.Ticked);
        Assert.Equal("PP-OCRv6_tiny_det", p.Active);
    }

    [Fact]
    public void PreselectLlmModels_EmptyWhenNothingInstalled()
    {
        var (ticked, locked, active) = OcrModelChoices.PreselectLlmModels(
            [], "Hy-MT2-1.8B-UD-Q4_K_XL", ["Hy-MT2-1.8B-UD-Q4_K_XL", "Hy-MT2-7B-Q5_K_M"]);

        Assert.Empty(ticked);
        Assert.Empty(locked);
        Assert.Null(active); // bước 4 mặc định để trống — optional
    }

    [Fact]
    public void PreselectLlmModels_InstalledTicked_ActiveFromCurrent()
    {
        string[] installed = ["Hy-MT2-1.8B-UD-Q4_K_XL"];
        var (ticked, locked, active) = OcrModelChoices.PreselectLlmModels(
            installed, "Hy-MT2-1.8B-UD-Q4_K_XL", ["Hy-MT2-1.8B-UD-Q4_K_XL", "Hy-MT2-7B-Q5_K_M"]);

        Assert.Equal(new[] { "Hy-MT2-1.8B-UD-Q4_K_XL" }, ticked);
        Assert.Equal(new[] { "Hy-MT2-1.8B-UD-Q4_K_XL" }, locked);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", active);
    }

    [Fact]
    public void PreselectLanguages_InstalledTickedLocked_ActiveFromSettings()
    {
        string[] installed = ["korean_PP-OCRv5_mobile_rec", "cyrillic_PP-OCRv5_mobile_rec"];
        var (ticked, locked, active) = OcrModelChoices.PreselectLanguages(installed, "korean_PP-OCRv5_mobile_rec");

        Assert.Equal(new[] { "korean_PP-OCRv5_mobile_rec", "cyrillic_PP-OCRv5_mobile_rec" }, ticked); // thứ tự nhóm
        Assert.Equal(ticked, locked);
        Assert.Equal("korean_PP-OCRv5_mobile_rec", active);
    }

    [Fact]
    public void DefaultVariant_PrefersInstalled()
    {
        var choice = new LanguageChoice("ko", ["korean_mobile_rec_x", "korean_server_rec_y"]);

        Assert.Equal("korean_server_rec_y", OcrModelChoices.DefaultVariant(choice, ["korean_server_rec_y"]));
        Assert.Equal("korean_mobile_rec_x", OcrModelChoices.DefaultVariant(choice, []));
    }

    [Fact]
    public void NeededModels_Union_NoDup()
    {
        var needed = OcrModelChoices.NeededModels(
            ["PP-OCRv6_small_det"], ["PP-OCRv6_small_rec"], ["korean_PP-OCRv5_mobile_rec"]);

        Assert.Equal(new[] { "PP-OCRv6_small_det", "PP-OCRv6_small_rec", "korean_PP-OCRv5_mobile_rec" }, needed);

        // Trùng giữa recs và langs → giữ một.
        var dedup = OcrModelChoices.NeededModels([], ["korean_PP-OCRv5_mobile_rec"], ["korean_PP-OCRv5_mobile_rec"]);
        Assert.Equal(new[] { "korean_PP-OCRv5_mobile_rec" }, dedup);
    }

    [Fact]
    public void ExpectedPair_LegacyWins_And_FallbackRec()
    {
        Assert.Equal(
            ("PP-OCRv6_medium_det", "korean_PP-OCRv5_mobile_rec"),
            OcrModelChoices.ExpectedPair("PP-OCRv6_medium_det", "korean_PP-OCRv5_mobile_rec", "PP-OCRv6_medium_rec"));
        Assert.Equal(
            ("PP-OCRv6_medium_det", "PP-OCRv6_medium_rec"),
            OcrModelChoices.ExpectedPair("PP-OCRv6_medium_det", null, "PP-OCRv6_medium_rec"));
    }
}
