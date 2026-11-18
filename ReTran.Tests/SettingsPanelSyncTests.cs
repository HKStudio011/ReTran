using ReTran_App.Core;

namespace ReTran.Tests;

public class SettingsPanelSyncTests
{
    private static readonly IReadOnlyList<OcrModelInfo> Models =
    [
        new("PP-OCRv6_medium_det", "det", [], 59.4, true, true),
        new("PP-OCRv6_medium_rec", "rec", [], 73.3, true, true),
        new("PP-OCRv6_small_det", "det", [], 9.6, true, false),
        new("PP-OCRv6_small_rec", "rec", [], 20.4, true, false),
    ];

    [Fact]
    public void OcrPicks_CatalogReloadWithoutActiveSync_PreservesDeferredPair()
    {
        (string det, string rec) = SettingsPanelSync.OcrPicks(
            Models, "PP-OCRv6_small_det", "PP-OCRv6_small_rec", syncFromActive: false);

        Assert.Equal("PP-OCRv6_small_det", det);
        Assert.Equal("PP-OCRv6_small_rec", rec);
    }

    [Fact]
    public void OcrPicks_SuccessfulImmediateApply_UsesActivePair()
    {
        (string det, string rec) = SettingsPanelSync.OcrPicks(
            Models, "PP-OCRv6_small_det", "PP-OCRv6_small_rec", syncFromActive: true);

        Assert.Equal("PP-OCRv6_medium_det", det);
        Assert.Equal("PP-OCRv6_medium_rec", rec);
    }

    [Fact]
    public void MergeWizardField_CleanValue_UsesWizardValueAndBaseline()
    {
        (string value, string baseline) = SettingsPanelSync.MergeWizardField(
            current: "old-model", baseline: "old-model", incoming: "wizard-model");

        Assert.Equal("wizard-model", value);
        Assert.Equal("wizard-model", baseline);
    }

    [Fact]
    public void MergeWizardField_DirtyValue_PreservesEditAndOriginalBaseline()
    {
        (string value, string baseline) = SettingsPanelSync.MergeWizardField(
            current: "unsaved-edit", baseline: "old-model", incoming: "wizard-model");

        Assert.Equal("unsaved-edit", value);
        Assert.Equal("old-model", baseline);
    }

    [Fact]
    public void WizardCompletionTransition_OnlyRisingEdgeNeedsResync()
    {
        Assert.False(SettingsPanelSync.WizardCompletionTransition(wasComplete: false, isComplete: false));
        Assert.True(SettingsPanelSync.WizardCompletionTransition(wasComplete: false, isComplete: true));
        Assert.False(SettingsPanelSync.WizardCompletionTransition(wasComplete: true, isComplete: true));
    }

    [Fact]
    public void LlmCatalogReload_EmptyResponse_KeepsLastValidCatalogAndReportsFailure()
    {
        IReadOnlyList<LlmModelInfo> current =
        [
            new("Hy-MT2-1.8B-Q4_K_M", "Q4_K_M", "1.8B", 1_170_000_000, true, true, false),
        ];

        (IReadOnlyList<LlmModelInfo>? models, bool failed) =
            SettingsPanelSync.LlmCatalogReload(current, []);

        Assert.Same(current, models);
        Assert.True(failed);
    }

    [Fact]
    public void LlmCatalogReload_EmptyFirstResponse_StaysInFailedLoadingState()
    {
        (IReadOnlyList<LlmModelInfo>? models, bool failed) =
            SettingsPanelSync.LlmCatalogReload(current: null, []);

        Assert.Null(models);
        Assert.True(failed);
    }

    [Fact]
    public void LlmCatalogReload_NonEmptyResponse_ReplacesCatalogAndClearsFailure()
    {
        IReadOnlyList<LlmModelInfo> incoming =
        [
            new("Hy-MT2-1.8B-Q4_K_M", "Q4_K_M", "1.8B", 1_170_000_000, true, true, false),
        ];

        (IReadOnlyList<LlmModelInfo>? models, bool failed) =
            SettingsPanelSync.LlmCatalogReload(current: null, incoming);

        Assert.Same(incoming, models);
        Assert.False(failed);
    }
}
