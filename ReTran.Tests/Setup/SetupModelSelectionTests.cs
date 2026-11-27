using ReTran_App.Services;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for pure setup completion, selected-artifact integrity and post-deletion selection rules.
/// Test cho các quy tắc thuần về hoàn tất setup, artifact đã chọn và lựa chọn sau khi xoá model.
/// </summary>
public sealed class SetupModelSelectionTests : IDisposable
{
    private const string SmallDet = "PP-OCRv6_small_det";
    private const string MediumDet = "PP-OCRv6_medium_det";
    private const string SmallRec = "PP-OCRv6_small_rec";
    private const string KoreanRec = "korean_PP-OCRv5_mobile_rec";
    private const string Llm = "Hy-MT2-1.8B-UD-Q4_K_XL";
    private const string OtherLlm = "Hy-MT2-7B-Q5_K_M";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"retran-setup-selection-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void HasCompleteAnswers_RejectsMissingRecsAndInvalidActiveFields()
    {
        SetupState valid = ValidState();
        Assert.True(SetupModelSelection.HasCompleteAnswers(valid));
        Assert.True(SetupModelSelection.HasCompleteAnswers(valid with
        {
            Langs = [],
            ActiveLang = null,
            LlmModels = [],
            ActiveLlm = null,
        }));

        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { Recs = null }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { Recs = [] }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { Dets = [] }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { ActiveDet = MediumDet }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { ActiveRec = null }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { ActiveLang = SmallRec }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { Langs = [] }));
        Assert.False(SetupModelSelection.HasCompleteAnswers(valid with { ActiveLlm = OtherLlm }));
    }

    [Fact]
    public void ArtifactsPresent_ValidSelectedArtifacts_ReturnsTrue()
    {
        SetupState state = ValidState() with { Dets = [SmallDet, MediumDet] };
        ArtifactFixture fixture = CreateArtifacts(state);

        Assert.True(ArtifactsPresent(state, fixture));
    }

    [Theory]
    [InlineData("venv")]
    [InlineData("ocr")]
    [InlineData("ocr-partial")]
    [InlineData("gguf")]
    [InlineData("gguf-partial")]
    [InlineData("runtime")]
    public void ArtifactsPresent_SelectedArtifactMissing_ReturnsFalse(string missing)
    {
        SetupState state = ValidState() with { Dets = [SmallDet, MediumDet] };
        ArtifactFixture fixture = CreateArtifacts(state);
        string selectedOcrDir = Path.Combine(fixture.ModelsDir, "official_models", MediumDet);
        string gguf = Path.Combine(fixture.LlmRoot, "models", Llm + ".gguf");

        switch (missing)
        {
            case "venv":
                File.Delete(fixture.VenvPath);
                break;
            case "ocr":
                Directory.Delete(selectedOcrDir, recursive: true);
                break;
            case "ocr-partial":
                File.WriteAllText(selectedOcrDir + ".partial", "partial");
                break;
            case "gguf":
                File.Delete(gguf);
                break;
            case "gguf-partial":
                File.WriteAllText(gguf + ".partial", "partial");
                break;
            case "runtime":
                File.Delete(fixture.ServerPath);
                break;
        }

        Assert.False(ArtifactsPresent(state, fixture));
    }

    [Fact]
    public void AfterDelete_RemovesEveryReference_AndPrefersEngineActive()
    {
        const string deleted = "deleted-model";
        SetupState state = ValidState() with
        {
            Dets = [deleted, SmallDet, MediumDet],
            ActiveDet = deleted,
            Recs = [deleted, SmallRec, "PP-OCRv6_medium_rec"],
            ActiveRec = deleted,
            Langs = [deleted, KoreanRec, "cyrillic_PP-OCRv5_mobile_rec"],
            ActiveLang = deleted,
            LlmModels = [deleted, Llm, OtherLlm],
            ActiveLlm = deleted,
        };
        AppSettings settings = AppSettings.Default with
        {
            OcrDet = deleted,
            OcrRec = deleted,
            LlmActiveModel = deleted,
        };
        string[] installed = [SmallDet, MediumDet, SmallRec, "PP-OCRv6_medium_rec", KoreanRec,
            "cyrillic_PP-OCRv5_mobile_rec", Llm, OtherLlm];
        // Rec has no engine-active candidate and therefore falls back to the first installed selection.
        string[] engineActive = [MediumDet, "cyrillic_PP-OCRv5_mobile_rec", OtherLlm];

        (SetupState setup, AppSettings saved) = SetupModelSelection.AfterDelete(
            state, settings, deleted, installed, engineActive);

        Assert.Equal([SmallDet, MediumDet], setup.Dets);
        Assert.Equal([SmallRec, "PP-OCRv6_medium_rec"], setup.Recs);
        Assert.Equal([KoreanRec, "cyrillic_PP-OCRv5_mobile_rec"], setup.Langs);
        Assert.Equal([Llm, OtherLlm], setup.LlmModels);
        Assert.Equal(MediumDet, setup.ActiveDet);
        Assert.Equal(SmallRec, setup.ActiveRec);
        Assert.Equal("cyrillic_PP-OCRv5_mobile_rec", setup.ActiveLang);
        Assert.Equal(OtherLlm, setup.ActiveLlm);
        Assert.Equal(MediumDet, saved.OcrDet);
        Assert.Equal("cyrillic_PP-OCRv5_mobile_rec", saved.OcrRec);
        Assert.Equal(OtherLlm, saved.LlmActiveModel);
        Assert.True(setup.IsComplete);
    }

    [Fact]
    public void AfterDelete_LastActiveLanguage_MarksLanguageUnanswered()
    {
        SetupState state = ValidState();
        AppSettings settings = AppSettings.Default with
        {
            OcrDet = SmallDet,
            OcrRec = KoreanRec,
            LlmActiveModel = Llm,
        };

        (SetupState setup, AppSettings saved) = SetupModelSelection.AfterDelete(
            state, settings, KoreanRec, [SmallDet, SmallRec, Llm], [SmallDet, SmallRec, Llm]);

        Assert.Null(setup.Langs);
        Assert.Null(setup.ActiveLang);
        Assert.Equal(SmallRec, saved.OcrRec);
        Assert.False(setup.IsComplete);
        Assert.False(SetupModelSelection.HasCompleteAnswers(setup));
    }

    [Fact]
    public void AfterDelete_NoCandidate_ClearsSettingsAndFailsClosed()
    {
        const string deleted = "deleted-model";
        SetupState state = ValidState() with
        {
            Dets = [deleted],
            ActiveDet = deleted,
            LlmModels = [deleted],
            ActiveLlm = deleted,
        };
        AppSettings settings = AppSettings.Default with
        {
            OcrDet = deleted,
            LlmActiveModel = deleted,
        };

        (SetupState setup, AppSettings saved) = SetupModelSelection.AfterDelete(
            state, settings, deleted, [SmallRec], [SmallRec]);

        Assert.Empty(setup.Dets!);
        Assert.Empty(setup.LlmModels!);
        Assert.Equal(string.Empty, setup.ActiveDet);
        Assert.Equal(string.Empty, setup.ActiveLlm);
        Assert.Equal(string.Empty, saved.OcrDet);
        Assert.Equal(string.Empty, saved.LlmActiveModel);
        Assert.False(setup.IsComplete);
        Assert.False(SetupModelSelection.HasCompleteAnswers(setup));
    }

    private static SetupState ValidState() => new()
    {
        Dets = [SmallDet],
        ActiveDet = SmallDet,
        Recs = [SmallRec],
        ActiveRec = SmallRec,
        Langs = [KoreanRec],
        ActiveLang = KoreanRec,
        LlmModels = [Llm],
        ActiveLlm = Llm,
        IsComplete = true,
    };

    private ArtifactFixture CreateArtifacts(SetupState state)
    {
        Directory.CreateDirectory(_dir);
        string venv = Path.Combine(_dir, ".venv", "Scripts", "python.exe");
        string models = Path.Combine(_dir, "models");
        string llmRoot = Path.Combine(_dir, "llm");
        WriteFile(venv);

        foreach (string name in OcrModelChoices.NeededModels(
                     state.Dets ?? [], state.Recs ?? [], state.Langs ?? [])
                     .Append(MediumDet)
                     .Distinct(StringComparer.Ordinal))
        {
            string modelDir = Path.Combine(models, "official_models", name);
            Directory.CreateDirectory(modelDir);
            File.WriteAllText(Path.Combine(modelDir, "inference.pdmodel"), "fixture");
        }

        WriteFile(Path.Combine(llmRoot, "models", Llm + ".gguf"));
        const string tag = "b12345";
        const string flavor = "cuda";
        string server = Path.Combine(llmRoot, "runtime", flavor, tag, "llama-server.exe");
        WriteFile(server);
        WriteFile(Path.Combine(llmRoot, "runtime", "runtime.json"),
            $$"""{"tag":"{{tag}}","flavor":"{{flavor}}"}""");
        return new ArtifactFixture(venv, models, llmRoot, server);
    }

    private static bool ArtifactsPresent(SetupState state, ArtifactFixture fixture) =>
        SetupModelSelection.ArtifactsPresent(
            state,
            fixture.VenvPath,
            fixture.ModelsDir,
            fixture.LlmRoot,
            File.Exists,
            SetupCoordinator.IsModelDirInstalled,
            path => path.EndsWith("llm-models.json", StringComparison.Ordinal)
                ? throw new FileNotFoundException("The App package does not include Core's catalog.", path)
                : File.ReadAllText(path));

    private static void WriteFile(string path, string contents = "fixture")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private sealed record ArtifactFixture(
        string VenvPath, string ModelsDir, string LlmRoot, string ServerPath);
}
