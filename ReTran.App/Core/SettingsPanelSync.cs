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

    /// <summary>Returns true only for the not-complete to complete edge. Chỉ true ở cạnh chuyển từ chưa hoàn tất sang hoàn tất.</summary>
    internal static bool WizardCompletionTransition(bool wasComplete, bool isComplete) =>
        !wasComplete && isComplete;
}
