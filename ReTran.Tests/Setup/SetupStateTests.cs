using System.Text.Json;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="SetupState"/>: the NeedsSetup predicate (the modal's show condition,
/// spec §5.9) and the Load/Save cycle over the JSON file bound to each instance.
/// Test cho <see cref="SetupState"/>: vị từ NeedsSetup (điều kiện hiện modal, spec §5.9) và vòng
/// Load/Save với file JSON gắn theo path của instance.
/// </summary>
public class SetupStateNeedsSetupTests : IDisposable
{
    private readonly string _dir;

    public SetupStateNeedsSetupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"retran-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void NeedsSetup_True_WhenVenvMissing_EvenIfStateComplete()
    {
        SetupState state = CompleteState();

        Assert.True(SetupState.NeedsSetup(state, @"C:\nope\python.exe", MakeModelsDir()));
    }

    [Fact]
    public void NeedsSetup_True_WhenModelsDirEmpty()
    {
        SetupState state = CompleteState();
        string venv = MakeVenvPythonExe();

        // official_models tồn tại nhưng không có mục nào (model chưa tải / đã bị xoá).
        string modelsEmpty = Temp("models-empty");
        Directory.CreateDirectory(Path.Combine(modelsEmpty, "official_models"));
        Assert.True(SetupState.NeedsSetup(state, venv, modelsEmpty));

        // models dir tồn tại, thiếu hẳn official_models.
        string modelsNoOfficial = Temp("models-no-official");
        Directory.CreateDirectory(modelsNoOfficial);
        Assert.True(SetupState.NeedsSetup(state, venv, modelsNoOfficial));
    }

    [Fact]
    public void NeedsSetup_False_WhenCompleteAndFilesPresent()
    {
        SetupState state = CompleteState() with
        {
            Steps = new Dictionary<string, string> { ["syncing"] = "done" },
        };
        string venv = MakeVenvPythonExe();
        string models = MakeModelsDir();
        Assert.False(SetupState.NeedsSetup(state, venv, models));

        // Cùng đủ file trên đĩa nhưng wizard chưa tới bước Done → vẫn phải hiện modal.
        Assert.True(SetupState.NeedsSetup(state with { IsComplete = false }, venv, models));
    }

    [Fact]
    public void NeedsSetup_MissingSelectedModel_ReturnsTrue()
    {
        SetupState state = CompleteState() with { Dets = ["PP-OCRv6_small_det", "PP-OCRv6_medium_det"] };
        string venv = MakeVenvPythonExe();
        string models = MakeModelsDir();

        Assert.True(SetupState.NeedsSetup(state, venv, models));
    }

    [Fact]
    public void Load_InvalidV2ActiveFields_DropsCompleteFlag()
    {
        string json = Temp("setup-invalid-active.json");
        File.WriteAllText(json, """
            {"flavor":"gpu","language":"vi","dets":["PP-OCRv6_small_det"],"activeDet":"PP-OCRv6_small_det","recs":["PP-OCRv6_small_rec"],"activeRec":"PP-OCRv6_small_rec","langs":[],"activeLang":"korean_PP-OCRv5_mobile_rec","llmModels":[],"isComplete":true}
            """);

        Assert.False(SetupState.Load(json).IsComplete);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyState()
    {
        string json = Temp("setup.json");
        Assert.False(File.Exists(json));

        SetupState state = SetupState.Load(json);

        Assert.Null(state.Flavor);
        Assert.Null(state.Language);
        Assert.Null(state.Steps);
        Assert.False(state.IsComplete);

        // State vẫn nhớ path nó được Load từ đó → Save ghi đúng vào file ấy.
        state.Save();
        Assert.True(File.Exists(json));
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_FlavorAndLanguage()
    {
        // Thư mục cha chưa tồn tại → Save phải tự tạo.
        string json = Path.Combine(_dir, "sub", "setup.json");

        SetupState saved = SetupState.Load(json) with
        {
            Flavor = "gpu",
            Language = "vi",
            IsComplete = true,
            // Hợp đồng v2: isComplete=true đòi hỏi mọi câu trả lời v2 đã có (màn trống = [], không
            // phải null) — NormalizeCompletion hạ cờ của file thiếu answer (test riêng phía dưới).
            Dets = ["PP-OCRv6_small_det"],
            ActiveDet = "PP-OCRv6_small_det",
            Recs = ["PP-OCRv6_small_rec"],
            ActiveRec = "PP-OCRv6_small_rec",
            Langs = [],
            LlmModels = [],
            Steps = new Dictionary<string, string> { ["syncing"] = "done" },
        };
        saved.Save();

        Assert.True(File.Exists(json));
        // Hình dạng file trên đĩa khớp spec §8 (khóa camelCase) — dev đọc/sửa tay được.
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json)))
        {
            Assert.Equal("gpu", doc.RootElement.GetProperty("flavor").GetString());
            Assert.True(doc.RootElement.GetProperty("isComplete").GetBoolean());
            Assert.Equal("done", doc.RootElement.GetProperty("steps").GetProperty("syncing").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            // Chốt đủ tập khóa gốc — khóa riêng (BoundPath) không được lọt vào JSON.
            Assert.Equal(
                new[]
                {
                    "activeDet", "activeLang", "activeLlm", "activeRec", "activeTier",
                    "dets", "extraLangs", "flavor", "isComplete",
                    "langs", "language", "llmModels", "recs", "steps", "tiers", "version",
                },
                doc.RootElement.EnumerateObject()
                    .Select(p => p.Name)
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToArray());
        }

        SetupState back = SetupState.Load(json);
        Assert.Equal("gpu", back.Flavor);
        Assert.Equal("vi", back.Language);
        Assert.True(back.IsComplete);
        Assert.Equal("done", back.Steps!["syncing"]);
        Assert.Equal(1, back.Version);

        // File cũ chưa có khóa version → vẫn đọc ra Version 1 (giá trị mặc định của property).
        string legacy = Temp("setup-legacy.json");
        File.WriteAllText(legacy, """{"flavor":"cpu","isComplete":true}""");
        Assert.Equal(1, SetupState.Load(legacy).Version);

        // Path gắn với instance sống qua `with` → bản mới vẫn Save ra đúng file đó.
        SetupState again = SetupState.Load(json) with { Language = "ja" };
        again.Save();
        Assert.Equal("ja", SetupState.Load(json).Language);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyState_NoThrow()
    {
        string json = Temp("setup.json");
        File.WriteAllText(json, "not json");

        SetupState state = SetupState.Load(json);

        Assert.Null(state.Flavor);
        Assert.Null(state.Language);
        Assert.False(state.IsComplete);

        // State hỏng vẫn nhớ path yêu cầu → Save ghi đè lại chính file đó.
        state.Save();
        Assert.True(File.Exists(json));
    }

    [Fact]
    public void Load_UnknownKey_Tolerated_AndDroppedOnSave()
    {
        string json = Temp("setup-unknown.json");
        File.WriteAllText(json, """{"flavor":"gpu","language":"vi","bogusStep":"x","version":1}""");

        // Khóa lạ được bỏ qua khi đọc (state vẫn đúng), và Save() chỉ ghi các trường đã biết.
        SetupState state = SetupState.Load(json);
        Assert.Equal("gpu", state.Flavor);
        Assert.Equal("vi", state.Language);

        state.Save();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
        Assert.False(doc.RootElement.TryGetProperty("bogusStep", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public void TiersActiveExtra_RoundTrip()
    {
        // Ba câu trả lời wizard 2026-11-16 sống qua Save → Load nguyên vẹn.
        string json = Temp("setup-answers.json");
        SetupState state = SetupState.Load(json) with
        {
            Tiers = ["small", "medium"],
            ActiveTier = "medium",
            ExtraLangs = ["ko", "ru"],
        };
        state.Save();

        SetupState back = SetupState.Load(json);
        Assert.Equal(new[] { "small", "medium" }, back.Tiers);
        Assert.Equal("medium", back.ActiveTier);
        Assert.Equal(new[] { "ko", "ru" }, back.ExtraLangs);
    }

    [Fact]
    public void LegacyJson_NullNewFields()
    {
        // setup.json của bản cũ (chưa có 3 khóa mới) → đọc ra null; wizard hỏi A/B một lượt (spec §8).
        string json = Temp("setup-old.json");
        File.WriteAllText(json, """{"flavor":"gpu","language":"vi","isComplete":true}""");

        SetupState state = SetupState.Load(json);

        Assert.Null(state.Tiers);
        Assert.Null(state.ActiveTier);
        Assert.Null(state.ExtraLangs);
    }

    [Fact]
    public void ExtraLangs_EmptyList_DistinctFromNull()
    {
        // null = "chưa hỏi màn B"; [] = "đã hỏi, chọn không" — hai nghĩa khác nhau, không được lẫn.
        string json = Temp("setup-extra.json");
        SetupState state = SetupState.Load(json);
        Assert.Null(state.ExtraLangs);

        state = state with { ExtraLangs = [] };
        state.Save();

        Assert.Empty(SetupState.Load(json).ExtraLangs!);
    }

    [Fact]
    public void V2Fields_RoundTrip()
    {
        string json = Temp("setup-v2.json");
        SetupState state = SetupState.Load(json) with
        {
            Dets = ["PP-OCRv6_small_det"],
            ActiveDet = "PP-OCRv6_small_det",
            Recs = ["PP-OCRv6_small_rec", "PP-OCRv6_medium_rec"],
            ActiveRec = "PP-OCRv6_small_rec",
            Langs = ["korean_PP-OCRv5_mobile_rec"],
            ActiveLang = "korean_PP-OCRv5_mobile_rec",
            LlmModels = ["Hy-MT2-1.8B-UD-Q4_K_XL"],
            ActiveLlm = "Hy-MT2-1.8B-UD-Q4_K_XL",
        };
        state.Save();

        SetupState back = SetupState.Load(json);

        Assert.Equal(new[] { "PP-OCRv6_small_det" }, back.Dets);
        Assert.Equal("PP-OCRv6_small_det", back.ActiveDet);
        Assert.Equal(new[] { "PP-OCRv6_small_rec", "PP-OCRv6_medium_rec" }, back.Recs);
        Assert.Equal("PP-OCRv6_small_rec", back.ActiveRec);
        Assert.Equal(new[] { "korean_PP-OCRv5_mobile_rec" }, back.Langs);
        Assert.Equal("korean_PP-OCRv5_mobile_rec", back.ActiveLang);
        Assert.Equal(new[] { "Hy-MT2-1.8B-UD-Q4_K_XL" }, back.LlmModels);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", back.ActiveLlm);
    }

    [Fact]
    public void Migration_OldFile_MapsNamesAndActiveLang()
    {
        // File 16-11: tiers = token cỡ, extraLangs = mã ngôn ngữ, language = ngôn ngữ chính.
        // Migration (một lần, in-memory) → tên model + ActiveLang giữ đúng cặp đang chạy cũ.
        string json = Temp("setup-old-v1.json");
        File.WriteAllText(json, """
            {"flavor":"gpu","language":"ko","tiers":["small","medium"],"activeTier":"small","extraLangs":["ru"]}
            """);

        SetupState state = SetupState.Load(json);

        Assert.Equal(new[] { "PP-OCRv6_small_det", "PP-OCRv6_medium_det" }, state.Dets);
        Assert.Equal(new[] { "PP-OCRv6_small_rec", "PP-OCRv6_medium_rec" }, state.Recs);
        Assert.Equal("PP-OCRv6_small_det", state.ActiveDet);
        Assert.Equal("PP-OCRv6_small_rec", state.ActiveRec);
        // extraLangs cũ là mã ngôn ngữ → map sang tên rec; language legacy (ko) thêm vào + thành ActiveLang.
        Assert.Equal(new[] { "cyrillic_PP-OCRv5_mobile_rec", "korean_PP-OCRv5_mobile_rec" }, state.Langs);
        Assert.Equal("korean_PP-OCRv5_mobile_rec", state.ActiveLang);
        Assert.Null(state.LlmModels); // chưa hỏi bước 4 — wizard sẽ hỏi một lượt
    }

    [Fact]
    public void Migration_OldFile_AutoOrV6Language_ActiveLangNull()
    {
        string json = Temp("setup-old-v2.json");
        File.WriteAllText(json, """{"flavor":"gpu","language":"vi","tiers":["small"],"activeTier":"small","extraLangs":[]}""");

        SetupState state = SetupState.Load(json);

        Assert.Equal(new[] { "PP-OCRv6_small_det" }, state.Dets);
        Assert.Empty(state.Langs!); // đã hỏi "không chọn"; language "vi" phủ v6 → không thêm, không ActiveLang
        Assert.Null(state.ActiveLang);
    }

    [Fact]
    public void Migration_OldFile_NoExtraLangs_LangsStaysNull()
    {
        // File cũ dừng trước màn B (extraLangs chưa từng được ghi) và không có ngôn ngữ legacy
        // ⇒ "chưa hỏi", không phải "đã hỏi: không chọn" — bản mới sẽ hỏi màn Ngôn ngữ một lượt.
        string json = Temp("setup-old-v4.json");
        File.WriteAllText(json, """{"flavor":"gpu","language":"vi","tiers":["small"],"activeTier":"small"}""");

        SetupState state = SetupState.Load(json);

        Assert.Equal(new[] { "PP-OCRv6_small_det" }, state.Dets);
        Assert.Null(state.Langs);
        Assert.Null(state.ActiveLang);
    }

    [Fact]
    public void LegacyJson_NewFieldsNull()
    {
        string json = Temp("setup-old-v3.json");
        File.WriteAllText(json, """{"flavor":"gpu","language":"vi","isComplete":true}""");

        SetupState state = SetupState.Load(json);

        Assert.Null(state.Dets);
        Assert.Null(state.ActiveDet);
        Assert.Null(state.Recs);
        Assert.Null(state.ActiveRec);
        Assert.Null(state.Langs);
        Assert.Null(state.ActiveLang);
        Assert.Null(state.LlmModels);
        Assert.Null(state.ActiveLlm);
    }

    [Fact]
    public void LegacyCompleteFlag_MissingV2Answers_LoadsIncomplete()
    {
        // File 14-11 hoàn tất theo wizard CŨ (isComplete=true, không tiers/dets): wizard v2 còn phải
        // hỏi det/rec + Ngôn ngữ + LLM — cờ cũ phải hạ xuống để wizard không bị ẩn vĩnh viễn
        // (constructor map state hoàn tất → Done). Lỗi thật 2026-11-17: "Chạy lại setup" xong det/rec,
        // Reload app → wizard biến mất dù langs/llmModels null.
        string json = Temp("setup-old-v5.json");
        File.WriteAllText(json, """
            {"flavor":"gpu","language":"auto","isComplete":true,"steps":{"checking":"done","syncing":"done","applying":"done"}}
            """);

        SetupState state = SetupState.Load(json);

        Assert.False(state.IsComplete);
        Assert.Null(state.Dets);
        Assert.Null(state.Langs);
        Assert.Null(state.LlmModels);
    }

    [Fact]
    public void Migration_16_11_TiersFile_DropsCompleteFlag()
    {
        // File 16-11 hoàn tất: migration map xong vẫn còn thiếu bước 4 (llmModels null) → isComplete
        // phải là false để wizard hỏi nốt; Done sẽ tự đặt lại.
        string json = Temp("setup-old-v6.json");
        File.WriteAllText(json, """
            {"flavor":"gpu","language":"vi","tiers":["small"],"activeTier":"small","extraLangs":[],"isComplete":true}
            """);

        SetupState state = SetupState.Load(json);

        Assert.False(state.IsComplete);
        Assert.Equal(["PP-OCRv6_small_det"], state.Dets);
        Assert.Null(state.LlmModels);
    }

    [Fact]
    public void V2_AllAnswered_KeepsCompleteFlag()
    {
        // State v2 trả lời ĐỦ (trả lời trống = [], không phải null) → cờ hoàn tất giữ nguyên.
        string json = Temp("setup-v2-complete.json");
        File.WriteAllText(json, """
            {"flavor":"gpu","language":"vi","dets":["PP-OCRv6_small_det"],"activeDet":"PP-OCRv6_small_det","recs":["PP-OCRv6_small_rec"],"activeRec":"PP-OCRv6_small_rec","langs":[],"llmModels":[],"isComplete":true,"steps":{"checking":"done","choosingocrmodels":"done","choosinglanguages":"done","choosingllmmodels":"done","verifyingfiles":"done","syncing":"done","verifyingsidecar":"done","downloadingllmruntime":"done","downloadingmodels":"done","applying":"done"}}
            """);

        SetupState state = SetupState.Load(json);

        Assert.True(state.IsComplete);
    }

    /// <summary>
    /// Creates <c>&lt;tmp&gt;\models\official_models</c> with one model file and returns the models dir.
    /// Tạo <c>&lt;tmp&gt;\models\official_models</c> kèm một file model, trả về models dir.
    /// </summary>
    private string MakeModelsDir()
    {
        string modelsDir = Temp("models");
        foreach (string name in new[] { "PP-OCRv6_small_det", "PP-OCRv6_small_rec" })
        {
            string modelDir = Path.Combine(modelsDir, "official_models", name);
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "inference.pdmodel"), "x");
        }
        return modelsDir;
    }

    private static SetupState CompleteState() => new()
    {
        Dets = ["PP-OCRv6_small_det"],
        ActiveDet = "PP-OCRv6_small_det",
        Recs = ["PP-OCRv6_small_rec"],
        ActiveRec = "PP-OCRv6_small_rec",
        Langs = [],
        ActiveLang = null,
        LlmModels = [],
        ActiveLlm = null,
        IsComplete = true,
    };

    /// <summary>
    /// Writes a fake <c>&lt;tmp&gt;\.venv\Scripts\python.exe</c> and returns its path.
    /// Ghi file giả <c>&lt;tmp&gt;\.venv\Scripts\python.exe</c>, trả về đường dẫn tới nó.
    /// </summary>
    private string MakeVenvPythonExe()
    {
        string exe = Path.Combine(_dir, ".venv", "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return exe;
    }

    private string Temp(string name) => Path.Combine(_dir, name);
}
