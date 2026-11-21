using System.Text.Json;
using ReTran_App.Services;

namespace ReTran_App.Services.Setup;

/// <summary>
/// Pure selection, completion and artifact-integrity rules shared by Setup and Settings.
/// Các quy tắc thuần về lựa chọn, hoàn tất và artifact dùng chung giữa Setup và Settings.
/// </summary>
public static class SetupModelSelection
{
    /// <summary>
    /// Returns whether every wizard answer and active pick is internally valid.
    /// Trả về mọi câu trả lời wizard và lựa chọn chính có hợp lệ nhất quán hay không.
    /// </summary>
    /// <param name="state">Setup answers to validate. Câu trả lời Setup cần kiểm tra.</param>
    /// <returns><see langword="true"/> when det/rec are selected, every active pick belongs to its list, and optional language/LLM answers are valid. <see langword="true"/> khi có det/rec, mọi lựa chọn chính thuộc danh sách tương ứng và các câu trả lời ngôn ngữ/LLM tuỳ chọn hợp lệ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is null. Ném khi <paramref name="state"/> là null.</exception>
    public static bool HasCompleteAnswers(SetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Dets is { Count: > 0 }
            && state.Recs is { Count: > 0 }
            && state.Langs is not null
            && state.LlmModels is not null
            && state.ActiveDet is not null
            && state.Dets.Contains(state.ActiveDet, StringComparer.Ordinal)
            && state.ActiveRec is not null
            && state.Recs.Contains(state.ActiveRec, StringComparer.Ordinal)
            && (state.ActiveLang is null
                || state.Langs.Contains(state.ActiveLang, StringComparer.Ordinal))
            && (state.ActiveLlm is null
                || state.LlmModels.Contains(state.ActiveLlm, StringComparer.Ordinal));
    }

    /// <summary>
    /// Checks only the artifacts required by the saved selections, without spawning a process or using the network.
    /// Chỉ kiểm tra artifact cần bởi các lựa chọn đã lưu, không khởi tạo process hoặc dùng mạng.
    /// </summary>
    /// <param name="state">Setup selections to check. Các lựa chọn Setup cần kiểm tra.</param>
    /// <param name="venvPath">Sidecar Python executable. Interpreter Python của sidecar.</param>
    /// <param name="modelsDir">OCR model cache root. Thư mục gốc cache model OCR.</param>
    /// <param name="llmRoot">Local LLM data root containing models and runtime. Thư mục gốc LLM local chứa models và runtime.</param>
    /// <param name="fileExists">File-existence seam. Seam kiểm tra file tồn tại.</param>
    /// <param name="modelDirExists">Installed-OCR-directory seam. Seam kiểm tra thư mục OCR đã cài.</param>
    /// <param name="readText">Text-read seam used for runtime.json. Seam đọc text dùng cho runtime.json.</param>
    /// <returns><see langword="true"/> only when every selected artifact is complete. <see langword="true"/> chỉ khi mọi artifact đã chọn đều hoàn chỉnh.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null. Ném khi một tham số bắt buộc là null.</exception>
    public static bool ArtifactsPresent(
        SetupState state,
        string venvPath,
        string modelsDir,
        string llmRoot,
        Func<string, bool> fileExists,
        Func<string, bool> modelDirExists,
        Func<string, string> readText)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(venvPath);
        ArgumentNullException.ThrowIfNull(modelsDir);
        ArgumentNullException.ThrowIfNull(llmRoot);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(modelDirExists);
        ArgumentNullException.ThrowIfNull(readText);
        if (!HasCompleteAnswers(state)) return false;
        List<string> dets = state.Dets!;
        List<string> recs = state.Recs!;
        List<string> langs = state.Langs!;
        List<string> llms = state.LlmModels!;

        try
        {
            if (!fileExists(venvPath)) return false;
            foreach (string name in OcrModelChoices.NeededModels(dets, recs, langs))
            {
                string modelDir = Path.Combine(modelsDir, SetupState.OfficialModelsSubdir, name);
                if (!modelDirExists(modelDir) || fileExists(modelDir + ".partial")) return false;
            }

            if (llms.Count == 0) return true;
            foreach (string name in llms)
            {
                if (!IsPathSegment(name)) return false;
                string path = Path.Combine(llmRoot, "models", name + ".gguf");
                if (!fileExists(path) || fileExists(path + ".partial")) return false;
            }

            string runtimeState = Path.Combine(llmRoot, "runtime", "runtime.json");
            using JsonDocument runtime = JsonDocument.Parse(readText(runtimeState));
            JsonElement root = runtime.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tag", out JsonElement tagValue)
                || !root.TryGetProperty("flavor", out JsonElement flavorValue))
            {
                return false;
            }

            string? tag = tagValue.GetString();
            string? flavor = flavorValue.GetString();
            if (!IsPathSegment(tag) || !IsPathSegment(flavor)) return false;
            return fileExists(Path.Combine(llmRoot, "runtime", flavor!, tag!, "llama-server.exe"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException
            or InvalidOperationException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a deleted model from setup and settings, choosing installed replacements in the same slots.
    /// Loại model đã xoá khỏi setup và settings, chọn model thay thế còn cài trong cùng slot.
    /// </summary>
    /// <param name="state">Current setup selections. Các lựa chọn Setup hiện tại.</param>
    /// <param name="settings">Current application settings. Settings hiện tại của app.</param>
    /// <param name="deletedName">Catalog name successfully deleted by the engine. Tên catalog engine đã xoá thành công.</param>
    /// <param name="installed">Catalog names still installed after deletion. Các tên catalog còn cài sau khi xoá.</param>
    /// <param name="engineActive">Models currently active in the engine. Các model engine đang dùng.</param>
    /// <returns>Updated setup and settings; engine-active candidates win over the first installed selection. Setup và settings đã cập nhật; candidate engine active được ưu tiên trước lựa chọn còn cài đầu tiên.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null. Ném khi một tham số bắt buộc là null.</exception>
    public static (SetupState Setup, AppSettings Settings) AfterDelete(
        SetupState state,
        AppSettings settings,
        string deletedName,
        IReadOnlyCollection<string> installed,
        IReadOnlyCollection<string> engineActive)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(deletedName);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(engineActive);

        List<string>? dets = Remove(state.Dets, deletedName);
        List<string>? recs = Remove(state.Recs, deletedName);
        List<string>? langs = Remove(state.Langs, deletedName);
        List<string>? llms = Remove(state.LlmModels, deletedName);
        bool failedClosed = false;

        string? activeDet = ReplaceActive(state.ActiveDet, dets, deletedName, installed, engineActive,
            ref failedClosed, emptyWhenMissing: true);
        string? activeRec = ReplaceActive(state.ActiveRec, recs, deletedName, installed, engineActive,
            ref failedClosed, emptyWhenMissing: true);
        string? activeLang = ReplaceActive(state.ActiveLang, langs, deletedName, installed, engineActive,
            ref failedClosed, emptyWhenMissing: false);
        string? activeLlm = ReplaceActive(state.ActiveLlm, llms, deletedName, installed, engineActive,
            ref failedClosed, emptyWhenMissing: true);
        if (string.Equals(state.ActiveLang, deletedName, StringComparison.Ordinal) && activeLang is null)
            langs = null; // no replacement means the optional language question is unanswered again
        if (langs is { Count: 0 }) activeLang = null;

        string ocrDet = settings.OcrDet;
        if (string.Equals(ocrDet, deletedName, StringComparison.Ordinal))
        {
            ocrDet = activeDet ?? string.Empty;
            failedClosed |= ocrDet.Length == 0;
        }

        string ocrRec = settings.OcrRec;
        if (string.Equals(ocrRec, deletedName, StringComparison.Ordinal))
        {
            ocrRec = activeLang ?? activeRec ?? string.Empty;
            failedClosed |= ocrRec.Length == 0;
        }

        string llmActive = settings.LlmActiveModel;
        if (string.Equals(llmActive, deletedName, StringComparison.Ordinal))
        {
            llmActive = activeLlm ?? string.Empty;
            failedClosed |= llmActive.Length == 0;
        }

        SetupState updated = state with
        {
            Dets = dets,
            ActiveDet = activeDet,
            Recs = recs,
            ActiveRec = activeRec,
            Langs = langs,
            ActiveLang = activeLang,
            LlmModels = llms,
            ActiveLlm = activeLlm,
        };
        updated = updated with
        {
            IsComplete = !failedClosed && state.IsComplete && HasCompleteAnswers(updated),
        };
        return (updated, settings with { OcrDet = ocrDet, OcrRec = ocrRec, LlmActiveModel = llmActive });
    }

    private static List<string>? Remove(List<string>? names, string deletedName) =>
        names?.Where(name => !string.Equals(name, deletedName, StringComparison.Ordinal)).ToList();

    private static string? ReplaceActive(
        string? current,
        List<string>? candidates,
        string deletedName,
        IReadOnlyCollection<string> installed,
        IReadOnlyCollection<string> engineActive,
        ref bool failedClosed,
        bool emptyWhenMissing)
    {
        if (!string.Equals(current, deletedName, StringComparison.Ordinal)) return current;
        string? replacement = candidates?
            .Where(candidate => installed.Contains(candidate, StringComparer.Ordinal))
            .OrderByDescending(candidate => engineActive.Contains(candidate, StringComparer.Ordinal))
            .FirstOrDefault();
        if (replacement is not null) return replacement;
        failedClosed = true;
        return emptyWhenMissing ? string.Empty : null;
    }

    private static bool IsPathSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal);
}
