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
/// <param name="Language">Legacy 16-11 main-language code, kept so the one-time migration can derive <see cref="Langs"/>/<see cref="ActiveLang"/>; the v2 wizard never writes it. Mã ngôn ngữ chính legacy từ file 16-11, giữ để migration một lần suy ra <see cref="Langs"/>/<see cref="ActiveLang"/>; wizard v2 không ghi nữa.</param>
/// <param name="Steps">Completed/failed steps keyed by step name (<c>done</c>/<c>failed</c>); <see langword="null"/> for a fresh state. Các bước đã xong/thất bại, khóa theo tên bước (<c>done</c>/<c>failed</c>); <see langword="null"/> với state mới.</param>
/// <param name="Tiers">Legacy 16-11 tier tokens, kept so the one-time migration can derive <see cref="Dets"/>/<see cref="Recs"/>; the v2 wizard never writes them. Token cỡ legacy từ file 16-11, giữ để migration một lần suy ra <see cref="Dets"/>/<see cref="Recs"/>; wizard v2 không ghi nữa.</param>
/// <param name="ActiveTier">Legacy 16-11 "Dùng chính" tier, kept so the one-time migration can derive <see cref="ActiveDet"/>/<see cref="ActiveRec"/>; the v2 wizard never writes it. Tier "Dùng chính" legacy từ file 16-11, giữ để migration một lần suy ra <see cref="ActiveDet"/>/<see cref="ActiveRec"/>; wizard v2 không ghi nữa.</param>
/// <param name="ExtraLangs">Legacy 16-11 extra-language codes, kept so the one-time migration can derive <see cref="Langs"/>; the v2 wizard never writes them. Mã ngôn ngữ bổ sung legacy từ file 16-11, giữ để migration một lần suy ra <see cref="Langs"/>; wizard v2 không ghi nữa.</param>
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
            return NormalizeCompletion(Migrate((state ?? new SetupState()) with { BoundPath = target }));
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
    /// <summary>
    /// Enforces the v2 completeness contract on load: <see cref="IsComplete"/> may only stay true when
    /// all four answer lists exist, det/rec are non-empty, and every active pick belongs to its list.
    /// Optional LLM may be empty; a language active pick must also belong to Langs. Invalid or pre-v2
    /// state loads incomplete so the wizard asks only what is missing and re-sets the flag at Done.
    /// Áp dụng hợp đồng hoàn tất v2 khi đọc: <see cref="IsComplete"/> chỉ được giữ true khi mọi câu
    /// trả lời v2 đều có (det/rec, Ngôn ngữ, LLM — trả lời trống là list rỗng, không phải null). File
    /// do bản trước v2 ghi có thể mang <c>isComplete: true</c> cũ trong khi vẫn còn câu hỏi v2, khiến
    /// constructor map sang Done và ẩn wizard vĩnh viễn; file như vậy đọc thành chưa hoàn tất để
    /// wizard hỏi lại phần thiếu và tự đặt lại cờ ở Done. Det/rec phải không rỗng và mọi active pick,
    /// kể cả ActiveLang/ActiveLlm, phải thuộc danh sách tương ứng mới được giữ cờ hoàn tất.
    /// </summary>
    private static SetupState NormalizeCompletion(SetupState state) =>
        state.IsComplete && !SetupModelSelection.HasCompleteAnswers(state)
            ? state with { IsComplete = false }
            : state;

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
    /// Whether the setup modal must run: the wizard has not finished, or selected artifacts are gone.
    /// Cheap by design — file/directory probes plus reading and parsing the active runtime.json;
    /// it never spawns Python/Core or uses the network.
    /// Wizard có phải chạy lại hay không: wizard chưa xong hoặc artifact đã chọn đã biến mất. Preflight
    /// rẻ gồm probe file/thư mục và đọc/parse runtime.json active; không spawn Python/Core hoặc dùng mạng.
    /// </summary>
    /// <param name="state">State loaded from <c>setup.json</c>. Trạng thái đọc từ <c>setup.json</c>.</param>
    /// <param name="venvPythonExe">Sidecar venv interpreter to test for existence. Đường dẫn interpreter venv sidecar cần kiểm tra.</param>
    /// <param name="modelsDir">OCR model cache root containing the selected model directories. Thư mục gốc cache OCR chứa các thư mục model đã chọn.</param>
    /// <param name="llmRoot">Local LLM root, or <see langword="null"/> for <see cref="SetupPaths.LlmRoot"/>. Thư mục gốc LLM local, hoặc <see langword="null"/> để dùng <see cref="SetupPaths.LlmRoot"/>.</param>
    /// <returns><see langword="true"/> when answers or any selected artifact require setup or repair. <see langword="true"/> khi câu trả lời hoặc một artifact đã chọn cần chạy setup/sửa.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="state"/> is null. Ném khi <paramref name="state"/> là null.</exception>
    public static bool NeedsSetup(
        SetupState state, string venvPythonExe, string modelsDir, string? llmRoot = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        return !state.IsComplete
            || !SetupModelSelection.HasCompleteAnswers(state)
            || !SetupModelSelection.ArtifactsPresent(
                state, venvPythonExe, modelsDir, llmRoot ?? SetupPaths.LlmRoot,
                File.Exists, SetupCoordinator.IsModelDirInstalled, File.ReadAllText);
    }
}
