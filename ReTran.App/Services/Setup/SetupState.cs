using System.Text.Json;

namespace ReTran_App.Services.Setup;

/// <summary>
/// First-run wizard progress persisted in <c>setup.json</c>: the OCR flavor chosen, the model answers
/// (det + rec names, legacy language recs, LLM models) with their active picks, which steps already
/// finished, and the schema version of the file — enough to resume the wizard where it stopped.
/// A missing or malformed file means "start over": every real artifact is re-checked on disk.
/// A 16-11 file (tier tokens + language codes) is migrated in memory on load.
/// Trạng thái wizard chạy lần đầu lưu trong <c>setup.json</c>: flavor OCR đã chọn, các câu trả lời
/// model (tên det + rec, rec ngôn ngữ legacy, model LLM) kèm lựa chọn "chạy chính", những bước đã
/// xong, và phiên bản schema của file — đủ để wizard chạy tiếp đúng chỗ nó dừng.
/// File thiếu hoặc hỏng nghĩa là "làm lại từ đầu": mọi thứ thật đều được kiểm tra lại trên đĩa.
/// File từ 16-11 (token cỡ + mã ngôn ngữ) được migration trong bộ nhớ khi đọc.
/// </summary>
/// <param name="Flavor">OCR flavor as written by the wizard (<c>gpu</c>/<c>cpu</c>); <see langword="null"/> before the check step decides. Flavor OCR wizard ghi (<c>gpu</c>/<c>cpu</c>); <see langword="null"/> trước khi bước check quyết định.</param>
/// <param name="Language">Language code chosen in the wizard; <see langword="null"/> until the user answers. Mã ngôn ngữ user chọn trong wizard; <see langword="null"/> cho tới khi user trả lời.</param>
/// <param name="Steps">Completed/failed steps keyed by step name (<c>done</c>/<c>failed</c>); <see langword="null"/> for a fresh state. Các bước đã xong/thất bại, khóa theo tên bước (<c>done</c>/<c>failed</c>); <see langword="null"/> với state mới.</param>
/// <param name="Tiers">OCR model tiers ticked on the "Chọn model OCR" screen (<c>tiny</c>/<c>small</c>/<c>medium</c>, in that order); <see langword="null"/> until the user answers. Các tier model OCR đã tick ở màn "Chọn model OCR" (theo thứ tự đó); <see langword="null"/> cho tới khi user trả lời.</param>
/// <param name="ActiveTier">Tier chosen as "Dùng chính" — the pair the wizard runs; <see langword="null"/> until the user answers. Tier được chọn "Dùng chính" — cặp model wizard sẽ chạy; <see langword="null"/> cho tới khi user trả lời.</param>
/// <param name="ExtraLangs">Extra language codes ticked on the "Ngôn ngữ bổ sung" screen; <see langword="null"/> = not asked yet, empty list = asked and none chosen. Mã ngôn ngữ bổ sung đã tick ở màn "Ngôn ngữ bổ sung"; <see langword="null"/> = chưa hỏi, list rỗng = đã hỏi và không chọn.</param>
/// <param name="Dets">Det model names ticked in step 2 (e.g. <c>PP-OCRv6_small_det</c>); <see langword="null"/> until answered. Tên model det đã tick ở bước 2; <see langword="null"/> cho tới khi trả lời.</param>
/// <param name="ActiveDet">Det model marked "Dùng chính" (spec §4); <see langword="null"/> until answered. Model det được chấm "Dùng chính"; <see langword="null"/> cho tới khi trả lời.</param>
/// <param name="Recs">Rec model names ticked in step 2; <see langword="null"/> until answered. Tên model rec đã tick ở bước 2; <see langword="null"/> cho tới khi trả lời.</param>
/// <param name="ActiveRec">Rec model marked "Dùng chính"; overridden by <paramref name="ActiveLang"/> when set. Model rec được chấm "Dùng chính"; bị <paramref name="ActiveLang"/> thay thế khi có.</param>
/// <param name="Langs">Legacy rec model names ticked in step 3; empty list = asked and none chosen. Tên rec legacy đã tick ở bước 3; list rỗng = đã hỏi và không chọn.</param>
/// <param name="ActiveLang">Legacy rec marked "Đọc chính" — the running rec, or <see langword="null"/> when none. Rec legacy được chấm "Đọc chính" — rec đang chạy, hoặc <see langword="null"/> khi không chấm.</param>
/// <param name="LlmModels">LLM catalog names ticked in step 4; <see langword="null"/> until asked, empty list = asked and none chosen. Tên LLM đã tick ở bước 4; <see langword="null"/> = chưa hỏi, list rỗng = đã hỏi và không chọn.</param>
/// <param name="ActiveLlm">LLM model marked "Dùng chính", or <see langword="null"/>. Model LLM được chấm "Dùng chính", hoặc <see langword="null"/>.</param>
public sealed record SetupState(
    string? Flavor = null,
    string? Language = null,
    Dictionary<string, string>? Steps = null,
    List<string>? Tiers = null,
    string? ActiveTier = null,
    List<string>? ExtraLangs = null,
    List<string>? Dets = null,
    string? ActiveDet = null,
    List<string>? Recs = null,
    string? ActiveRec = null,
    List<string>? Langs = null,
    string? ActiveLang = null,
    List<string>? LlmModels = null,
    string? ActiveLlm = null)
{
    // internal để SystemCheck (checklist Checking) đếm model có sẵn theo cùng định nghĩa với NeedsSetup.
    // internal so SystemCheck (Checking checklist) counts installed models by the same definition as NeedsSetup.
    internal const string OfficialModelsSubdir = "official_models";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// True once the wizard has reached its Done step and saved that fact.
    /// <see langword="true"/> khi wizard đã đi tới bước Done và lưu lại điều đó.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    /// Schema version of the state file, written under the key <c>version</c> (spec §8). A fresh state
    /// starts at 1; a file written by an older app build that predates the key also reads as 1. Nothing
    /// acts on the value yet.
    /// Phiên bản schema của file trạng thái, ghi dưới khóa <c>version</c> (spec §8). State mới bắt đầu ở
    /// 1; file do bản app cũ ghi trước khi có khóa đó cũng đọc ra 1. Hiện chưa có logic nào dựa trên giá trị.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// File this instance is bound to, carried along by <c>with</c> expressions so a caller can update
    /// fields and still save back to the file it loaded. Not part of the JSON.
    /// File mà instance này gắn với, được giữ nguyên qua biểu thức <c>with</c> để nơi gọi sửa trường xong
    /// vẫn ghi lại đúng file nó đã đọc. Không nằm trong JSON.
    /// </summary>
    private string BoundPath { get; init; } = SetupPaths.SetupJsonPath;

    /// <summary>
    /// Reads <c>setup.json</c> at <paramref name="path"/> (default <see cref="SetupPaths.SetupJsonPath"/>);
    /// a missing, malformed or unreadable file returns a fresh <see cref="SetupState"/> that is still
    /// bound to that path. The returned state remembers the path, so <see cref="Save"/> writes back to it.
    /// Đọc <c>setup.json</c> tại <paramref name="path"/> (mặc định <see cref="SetupPaths.SetupJsonPath"/>);
    /// file thiếu, hỏng hoặc không đọc được trả về <see cref="SetupState"/> mới nhưng vẫn gắn với path đó.
    /// State trả về nhớ path, nên <see cref="Save"/> ghi ngược lại đúng file ấy.
    /// </summary>
    /// <param name="path">File to read, or <see langword="null"/> for the default location. File cần đọc, hoặc <see langword="null"/> để dùng vị trí mặc định.</param>
    /// <returns>The stored state, or a fresh one when the file cannot be used. Trạng thái đã lưu, hoặc trạng thái mới khi không dùng được file.</returns>
    public static SetupState Load(string? path = null)
    {
        string target = path ?? SetupPaths.SetupJsonPath;
        try
        {
            if (!File.Exists(target)) return new SetupState { BoundPath = target };
            SetupState? state =
                JsonSerializer.Deserialize<SetupState>(File.ReadAllText(target), SerializerOptions);
            return Migrate((state ?? new SetupState()) with { BoundPath = target });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SetupState { BoundPath = target };
        }
    }

    /// <summary>
    /// One-time in-memory mapping of a 16-11 wizard file (tier tokens + language codes + the main
    /// language) onto the v2 fields (model names); the file on disk is rewritten on the next Save.
    /// Migration MỘT LẦN trong bộ nhớ cho file wizard 16-11 (token cỡ + mã ngôn ngữ + ngôn ngữ
    /// chính) sang field v2 (tên model); file trên đĩa được ghi lại ở lần Save kế tiếp.
    /// </summary>
    private static SetupState Migrate(SetupState state)
    {
        if (state.Dets is not null || state.Tiers is null) return state;

        string? legacyRec = state.Language is { } lang
            && !OcrModelChoices.IsV6Covered(lang)
            ? OcrModelChoices.LegacyRecFor(lang)
            : null;

        var langs = new List<string>();
        foreach (string code in state.ExtraLangs ?? [])
        {
            if (OcrModelChoices.LegacyRecFor(code) is { } mapped && !langs.Contains(mapped)) langs.Add(mapped);
        }
        if (legacyRec is not null && !langs.Contains(legacyRec)) langs.Add(legacyRec);

        // extraLangs null (màn B chưa từng trả lời) và không có rec legacy ⇒ giữ Langs = null để màn
        // Ngôn ngữ được hỏi MỘT lượt ở bản mới — không biến "chưa hỏi" thành "đã hỏi: không chọn".
        List<string>? mappedLangs = state.ExtraLangs is null && legacyRec is null ? null : langs;

        string? det = state.ActiveTier is { } tier ? $"PP-OCRv6_{tier}_det" : null;
        string? rec = state.ActiveTier is { } tier2 ? $"PP-OCRv6_{tier2}_rec" : null;

        return state with
        {
            Dets = [.. state.Tiers.Select(t => $"PP-OCRv6_{t}_det")],
            Recs = [.. state.Tiers.Select(t => $"PP-OCRv6_{t}_rec")],
            ActiveDet = det,
            ActiveRec = rec,
            Langs = mappedLangs,
            ActiveLang = legacyRec,
        };
    }

    /// <summary>
    /// Writes the state to its bound path atomically (temp file, then move), creating the parent
    /// directory when missing.
    /// Ghi state ra path đã gắn theo kiểu nguyên tử (file tạm rồi move), tạo thư mục cha nếu chưa có.
    /// </summary>
    public void Save()
    {
        string? dir = Path.GetDirectoryName(BoundPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = BoundPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SerializerOptions));
        File.Move(tmp, BoundPath, overwrite: true);
    }

    /// <summary>
    /// Whether the setup modal must run: the wizard has not finished, or the artifacts it produced are
    /// gone from disk (deleted, overwritten by a fresh extraction). Cheap by design — only file and
    /// directory checks, never a Python probe.
    /// Wizard có phải chạy lại hay không: wizard chưa xong, hoặc các thư mục nó tạo ra đã biến mất trên
    /// đĩa (bị xoá, hoặc giải nén đè). Cố ý rẻ — chỉ kiểm tra file/thư mục, không probe Python.
    /// </summary>
    /// <param name="state">State loaded from <c>setup.json</c>. Trạng thái đọc từ <c>setup.json</c>.</param>
    /// <param name="venvPythonExe">Sidecar venv interpreter to test for existence. Đường dẫn interpreter venv sidecar cần kiểm tra.</param>
    /// <param name="modelsDir">Model cache root; its <c>official_models</c> subdirectory must be non-empty. Thư mục gốc cache model; thư mục con <c>official_models</c> phải không rỗng.</param>
    /// <returns><see langword="true"/> when setup has to run again. <see langword="true"/> khi setup phải chạy lại.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is null. Ném khi <paramref name="state"/> là null.</exception>
    public static bool NeedsSetup(SetupState state, string venvPythonExe, string modelsDir)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsComplete) return true;
        if (!File.Exists(venvPythonExe)) return true;

        string official = Path.Combine(modelsDir, OfficialModelsSubdir);
        return !Directory.Exists(official) || !Directory.EnumerateFileSystemEntries(official).Any();
    }
}
