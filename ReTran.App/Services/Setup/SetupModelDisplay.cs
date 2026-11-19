using ReTran_App.Core;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Pure presentation rules for the setup model list and download-step label.
/// Các quy tắc hiển thị thuần cho danh sách model và nhãn bước tải trong wizard.
/// </summary>
internal static class SetupModelDisplay
{
    /// <summary>Returns the LLM row status key, with active download taking precedence. Trả về key trạng thái LLM, ưu tiên lượt tải đang chạy.</summary>
    internal static string LlmStatusKey(LlmModelInfo model) =>
        model.Downloading ? "setup.llm.downloading"
            : model.Installed ? "setup.models.installed"
            : "setup.llm.notInstalled";

    /// <summary>Returns whether a row should announce its download size. Cho biết dòng có cần thông báo dung lượng sẽ tải không.</summary>
    internal static bool ShowLlmDownloadSize(LlmModelInfo model, bool selected) =>
        selected && !model.Installed && !model.Downloading;

    /// <summary>Returns whether a non-empty selection resolves entirely to installed catalog models. Cho biết tập chọn không rỗng có resolve toàn bộ thành model đã cài trong catalog không.</summary>
    internal static bool AllSelectedLlmInstalled(
        IEnumerable<LlmModelInfo> models, IReadOnlyCollection<string> selectedNames)
    {
        if (selectedNames.Count == 0) return false;
        var selected = models
            .Where(model => selectedNames.Contains(model.Name, StringComparer.Ordinal))
            .ToList();
        return selected.Count == selectedNames.Count && selected.All(model => model.Installed);
    }

    /// <summary>Returns the checking label until a real model download exists. Trả về nhãn kiểm tra cho tới khi có lượt tải model thật.</summary>
    internal static string DownloadingModelsStepKey(bool hasDownloads) =>
        hasDownloads ? "setup.step.downloadingModels" : "setup.step.checkingModels";
}
