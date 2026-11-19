using ReTran_App.Core;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for the pure presentation rules behind the setup model list and download-step label.
/// Test cho các quy tắc hiển thị thuần của danh sách model và nhãn bước tải trong wizard.
/// </summary>
public class SetupModelDisplayTests
{
    [Theory]
    [InlineData(true, false, "setup.models.installed")]
    [InlineData(false, false, "setup.llm.notInstalled")]
    [InlineData(false, true, "setup.llm.downloading")]
    [InlineData(true, true, "setup.llm.downloading")]
    public void LlmStatusKey_UsesModelState_NotLockOrTick(
        bool installed, bool downloading, string expected)
    {
        LlmModelInfo model = Model(installed, downloading);

        Assert.Equal(expected, SetupModelDisplay.LlmStatusKey(model));
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    public void ShowLlmDownloadSize_RequiresSelectedMissingIdleModel(
        bool installed, bool downloading, bool selected, bool expected)
    {
        LlmModelInfo model = Model(installed, downloading);

        Assert.Equal(expected, SetupModelDisplay.ShowLlmDownloadSize(model, selected));
    }

    [Fact]
    public void AllSelectedLlmInstalled_EmptySelection_ReturnsFalse()
    {
        Assert.False(SetupModelDisplay.AllSelectedLlmInstalled([Model(true, false)], []));
    }

    [Fact]
    public void AllSelectedLlmInstalled_UnknownSelection_ReturnsFalse()
    {
        Assert.False(SetupModelDisplay.AllSelectedLlmInstalled(
            [Model(true, false)], ["outside-catalog"]));
    }

    [Fact]
    public void AllSelectedLlmInstalled_AllSelectedInstalled_ReturnsTrue()
    {
        IReadOnlyList<LlmModelInfo> models =
        [
            Model(true, false, "model-a"),
            Model(true, false, "model-b"),
            Model(false, false, "model-c"),
        ];

        Assert.True(SetupModelDisplay.AllSelectedLlmInstalled(models, ["model-a", "model-b"]));
    }

    [Fact]
    public void AllSelectedLlmInstalled_SelectedModelMissing_ReturnsFalse()
    {
        IReadOnlyList<LlmModelInfo> models =
        [
            Model(true, false, "model-a"),
            Model(false, false, "model-b"),
        ];

        Assert.False(SetupModelDisplay.AllSelectedLlmInstalled(models, ["model-a", "model-b"]));
    }

    [Theory]
    [InlineData(false, "setup.step.checkingModels")]
    [InlineData(true, "setup.step.downloadingModels")]
    public void DownloadingModelsStepKey_DistinguishesCheckingFromRealDownloads(
        bool hasDownloads, string expected)
    {
        Assert.Equal(expected, SetupModelDisplay.DownloadingModelsStepKey(hasDownloads));
    }

    private static LlmModelInfo Model(
        bool installed, bool downloading, string name = "model-a") =>
        new(name, "Q4_K_M", "1.8B", 1_170_000_000, installed, false, downloading);
}
