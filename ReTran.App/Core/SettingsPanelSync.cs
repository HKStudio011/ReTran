using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using ReTran_App.Services;

namespace ReTran_App.Core;

/// <summary>
/// Pure state transitions for Settings catalog reloads and wizard completion.
/// Các chuyển trạng thái thuần khi Settings nạp catalog hoặc wizard hoàn tất.
/// </summary>
internal static class SettingsPanelSync
{
    /// <summary>
    /// Uses the sidecar's active pair only for an explicit active sync; ordinary catalog refreshes
    /// keep the user's deferred selection. Chỉ lấy cặp InUse khi được yêu cầu; refresh thường giữ
    /// lựa chọn đang hoãn của user.
    /// </summary>
    internal static (string Det, string Rec) OcrPicks(
        IReadOnlyList<OcrModelInfo> models, string currentDet, string currentRec, bool syncFromActive)
    {
        if (!syncFromActive) return (currentDet, currentRec);
        string det = models.FirstOrDefault(m => m.Kind == "det" && m.InUse)?.Name ?? currentDet;
        string rec = models.FirstOrDefault(m => m.Kind == "rec" && m.InUse)?.Name ?? currentRec;
        return (det, rec);
    }

    /// <summary>
    /// Applies one wizard-written field only when the user has not edited it since the last
    /// programmatic baseline. Chỉ áp field do wizard ghi khi user chưa sửa nó từ baseline gần nhất.
    /// </summary>
    internal static (string Value, string Baseline) MergeWizardField(
        string current, string baseline, string incoming) =>
        string.Equals(current, baseline, StringComparison.Ordinal)
            ? (incoming, incoming)
            : (current, baseline);

    /// <summary>
    /// Treats an empty LLM catalog as a failed load and keeps the last valid entries.
    /// Coi catalog LLM rỗng là nạp lỗi và giữ các mục hợp lệ gần nhất.
    /// </summary>
    internal static (IReadOnlyList<LlmModelInfo>? Models, bool Failed) LlmCatalogReload(
        IReadOnlyList<LlmModelInfo>? current, IReadOnlyList<LlmModelInfo> incoming) =>
        incoming.Count == 0 ? (current, true) : (incoming, false);

    /// <summary>
    /// Builds the settings saved after a successful OCR config update, preserving unrelated fields.
    /// Dựng settings sau khi cập nhật OCR thành công, giữ nguyên các field không liên quan.
    /// </summary>
    internal static AppSettings WithOcrParams(
        AppSettings current,
        Dictionary<string, JsonNode?> engine,
        int ocrFps,
        int captureFps,
        bool mergeLines,
        double mergeGap,
        double mergeHeight,
        double mergeOverlap) =>
        current with
        {
            OcrEngine = engine,
            OcrFps = ocrFps,
            CaptureFps = captureFps,
            OcrMergeLines = mergeLines,
            OcrMergeGap = mergeGap,
            OcrMergeHeight = mergeHeight,
            OcrMergeOverlap = mergeOverlap,
        };

    /// <summary>
    /// Requires a non-blank user template containing <c>{source_text}</c> for the LLM provider.
    /// Với provider LLM, bắt buộc template user không trắng và chứa <c>{source_text}</c>.
    /// </summary>
    internal static bool LlmUserPromptValid(string provider, string prompt) =>
        !string.Equals(provider, "llm", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(prompt) && prompt.Contains("{source_text}", StringComparison.Ordinal));

    /// <summary>Returns the supported translation provider, falling back to Google. Trả về provider dịch được hỗ trợ, mặc định về Google.</summary>
    internal static string NormalizeProvider(string? provider) =>
        string.Equals(provider, "llm", StringComparison.OrdinalIgnoreCase) ? "llm" : "google";

    /// <summary>Allows OCR replacement saves only with a trusted, non-empty schema. Chỉ cho Save thay thế OCR khi schema đáng tin và không rỗng.</summary>
    internal static bool CanSaveOcrParams([NotNullWhen(true)] IReadOnlyList<OcrParamSpec>? schema, bool schemaFailed) =>
        !schemaFailed && schema is { Count: > 0 };

    /// <summary>Returns whether a component may render after its disposal state changes. Cho biết component còn được render sau khi trạng thái dispose thay đổi.</summary>
    internal static bool ShouldRender(bool disposed) => !disposed;

    /// <summary>Returns true only for the not-complete to complete edge. Chỉ true ở cạnh chuyển từ chưa hoàn tất sang hoàn tất.</summary>
    internal static bool WizardCompletionTransition(bool wasComplete, bool isComplete) =>
        !wasComplete && isComplete;
}
