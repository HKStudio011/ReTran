using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReTran_App.Services;

/// <summary>
/// Translation settings persisted in <c>settings.json</c>: provider, language pair and an
/// optional API key for providers that need one, plus game-overlay display options, the OCR
/// model pair and engine parameter overrides requested on start, the pipeline rhythm, the
/// merge-lines toggle and its thresholds, the floating desktop menu state, and the last
/// capture source.
/// Cài đặt dịch thuật lưu trong <c>settings.json</c>: nhà cung cấp, cặp ngôn ngữ và API key tùy
/// chọn cho các nhà cung cấp cần key, kèm tùy chọn hiển thị overlay trên game, cặp model OCR,
/// các tham số engine ghi đè yêu cầu khi start, nhịp pipeline, công tắc gộp dòng cùng các ngưỡng
/// gộp, trạng thái menu nổi trên desktop, và nguồn capture đã dùng lần cuối.
/// </summary>
/// <param name="Provider">Provider name, e.g. <c>google</c>. Tên nhà cung cấp, ví dụ <c>google</c>.</param>
/// <param name="Src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
/// <param name="Dst">Target language code. Mã ngôn ngữ đích.</param>
/// <param name="ApiKey">API key for providers that require one; <see langword="null"/> otherwise. API key cho nhà cung cấp yêu cầu; ngược lại <see langword="null"/>.</param>
/// <param name="OverlayOnGame">Whether the local overlay window over the capture source is enabled. Có bật cửa sổ overlay local đè lên nguồn capture hay không.</param>
/// <param name="OcrDet">OCR detection model name sent to Core on <c>core.start</c>. Tên model det OCR gửi cho Core khi <c>core.start</c>.</param>
/// <param name="OcrRec">OCR recognition model name sent to Core on <c>core.start</c>. Tên model rec OCR gửi cho Core khi <c>core.start</c>.</param>
/// <param name="OcrEngine">
/// OCR engine parameter overrides keyed by PaddleOCR keyword, forwarded verbatim on
/// <c>core.start</c>; Core drops keys outside the sidecar schema with a warning.
/// <see langword="null"/> means no overrides — the sidecar keeps its current configuration.
/// Các tham số engine OCR ghi đè, khóa theo từ khóa PaddleOCR, gửi nguyên trạng trong
/// <c>core.start</c>; Core bỏ các khóa ngoài schema sidecar kèm cảnh báo.
/// <see langword="null"/> nghĩa là không ghi đè — sidecar giữ nguyên cấu hình hiện tại.
/// </param>
/// <param name="OcrFps">OCR rhythm in frames per second (1..60) sent on <c>core.start</c>. Nhịp OCR (khung/giây, 1..60) gửi khi <c>core.start</c>.</param>
/// <param name="CaptureFps">Capture rhythm in frames per second (1..60) sent on <c>core.start</c>. Nhịp capture (khung/giây, 1..60) gửi khi <c>core.start</c>.</param>
/// <param name="OcrMergeLines">Merge nearby OCR lines into one block before translation; sent on core.start as ocr.mergeLines. Gộp các dòng OCR gần nhau thành một khối trước khi dịch; gửi trong core.start dưới dạng ocr.mergeLines.</param>
/// <param name="FloatingMenuOn">Whether the floating desktop menu is shown; it is on by default so a fresh install sees the menu. Có bật menu nổi trên desktop hay không; mặc định bật để bản cài mới đã thấy menu.</param>
/// <param name="FloatingMenuCollapsed">Whether the floating menu is drawn as the compact chip instead of the full panel. Menu nổi được vẽ dạng chip thu gọn thay vì bảng đầy đủ.</param>
/// <param name="FloatingMenuX">Left edge of the floating menu in desktop pixels; <see langword="null"/> means place it at the default spot (top-right of the work area) on first run. Tọa độ trái của menu nổi tính theo pixel desktop; <see langword="null"/> nghĩa là tự đặt ở vị trí mặc định (góc trên phải vùng làm việc) ở lần chạy đầu.</param>
/// <param name="FloatingMenuY">Top edge of the floating menu in desktop pixels; <see langword="null"/> means the default spot on first run. Tọa độ trên của menu nổi tính theo pixel desktop; <see langword="null"/> nghĩa là vị trí mặc định ở lần chạy đầu.</param>
/// <param name="SourceUseWindow">Last capture source kind: <see langword="true"/> captures a window, <see langword="false"/> captures a monitor. Loại nguồn capture đã dùng lần cuối: <see langword="true"/> capture cửa sổ, <see langword="false"/> capture màn hình.</param>
/// <param name="SourceMonitor">Zero-based monitor index used when <paramref name="SourceUseWindow"/> is <see langword="false"/>; 0 is the primary monitor. Chỉ số màn hình (bắt đầu từ 0) dùng khi <paramref name="SourceUseWindow"/> là <see langword="false"/>; 0 là màn hình chính.</param>
/// <param name="SourceHwnd">Window handle of the last window source as a hex string (<c>0x…</c>); handles are only meaningful inside the run that reported them, so a stale value never starts a window capture on its own. Handle của cửa sổ nguồn gần nhất dạng chuỗi hex (<c>0x…</c>); handle chỉ có nghĩa trong lần chạy đã báo cáo nó, nên giá trị cũ không tự khởi động capture cửa sổ.</param>
/// <param name="OcrMergeGap">
/// Vertical gap between two lines, as a multiple of the shorter line's height, that still merges
/// them into one block; valid range <c>0.1..2.0</c>. Sent on <c>core.start</c> as <c>ocr.mergeGapRatio</c>.
/// Khoảng cách dọc giữa hai dòng, tính theo bội số chiều cao dòng thấp hơn, mà vẫn gộp thành một
/// khối; dải hợp lệ <c>0.1..2.0</c>. Gửi trong <c>core.start</c> dưới dạng <c>ocr.mergeGapRatio</c>.
/// </param>
/// <param name="OcrMergeHeight">
/// Maximum ratio of taller line height to shorter line height that still merges them into one
/// block; valid range <c>1.0..3.0</c>. Sent on <c>core.start</c> as <c>ocr.mergeHeightRatio</c>.
/// Tỉ lệ tối đa giữa chiều cao dòng cao hơn và dòng thấp hơn mà vẫn gộp thành một khối;
/// dải hợp lệ <c>1.0..3.0</c>. Gửi trong <c>core.start</c> dưới dạng <c>ocr.mergeHeightRatio</c>.
/// </param>
/// <param name="OcrMergeOverlap">
/// Minimum horizontal overlap of two lines, as a fraction of the narrower line's width, required to
/// treat them as the same column; valid range <c>0.0..1.0</c>, where <c>0.0</c> only requires the
/// two boxes to touch. Sent on <c>core.start</c> as <c>ocr.mergeOverlapRatio</c>.
/// Mức chồng nhau ngang tối thiểu giữa hai dòng, tính theo tỉ lệ chiều rộng dòng hẹp hơn, để coi
/// hai dòng cùng cột; dải hợp lệ <c>0.0..1.0</c>, với <c>0.0</c> chỉ đòi hai box chạm nhau.
/// Gửi trong <c>core.start</c> dưới dạng <c>ocr.mergeOverlapRatio</c>.
/// </param>
/// <param name="LlmMode">LLM mode: <c>local</c> (GGUF via llama.cpp) or <c>api</c> (OpenAI-compatible endpoint). Chế độ LLM: <c>local</c> (GGUF qua llama.cpp) hoặc <c>api</c> (endpoint OpenAI-compatible).</param>
/// <param name="LlmBaseUrl">Base URL for API mode. URL gốc cho chế độ API.</param>
/// <param name="LlmApiKey">Optional bearer key for API mode. Bearer key tuỳ chọn cho chế độ API.</param>
/// <param name="LlmApiModel">Model id sent to the API endpoint. Model id gửi tới endpoint API.</param>
/// <param name="LlmActiveModel">Catalog name of the installed GGUF used in local mode. Tên catalog của GGUF đã cài dùng ở chế độ local.</param>
/// <param name="LlmSystemPrompt">System prompt override; <see langword="null"/> keeps the Hy-MT2 default (empty). System prompt ghi đè; <see langword="null"/> giữ mặc định Hy-MT2 (rỗng).</param>
/// <param name="LlmUserPrompt">User prompt template override; <see langword="null"/> keeps the Hy-MT2 default. Template user prompt ghi đè; <see langword="null"/> giữ mặc định Hy-MT2.</param>
public sealed record AppSettings(
    string Provider, string Src, string Dst, string? ApiKey,
    bool OverlayOnGame = false,
    string OcrDet = "PP-OCRv6_small_det",
    string OcrRec = "PP-OCRv6_small_rec",
    Dictionary<string, JsonNode?>? OcrEngine = null,
    int OcrFps = 3,
    int CaptureFps = 10,
    bool OcrMergeLines = true,
    bool FloatingMenuOn = true,
    bool FloatingMenuCollapsed = false,
    int? FloatingMenuX = null,
    int? FloatingMenuY = null,
    bool SourceUseWindow = false,
    int SourceMonitor = 0,
    string? SourceHwnd = null,
    double OcrMergeGap = 0.6,
    double OcrMergeHeight = 1.35,
    double OcrMergeOverlap = 0.5,
    string LlmMode = "local",
    string LlmBaseUrl = "",
    string? LlmApiKey = null,
    string LlmApiModel = "Hy-MT2-1.8B",
    string LlmActiveModel = "Hy-MT2-1.8B-UD-Q4_K_XL",
    string? LlmSystemPrompt = null,
    string? LlmUserPrompt = null)
{
    /// <summary>
    /// Factory default: Google Translate, auto source, Vietnamese target, no API key.
    /// Mặc định từ đầu: Google Translate, nguồn auto, đích tiếng Việt, không API key.
    /// </summary>
    public static AppSettings Default { get; } = new("google", "auto", "vi", null);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON at a fixed path (default
/// <c>%LOCALAPPDATA%\ReTran\settings.json</c>). A missing or unreadable file yields
/// <see cref="AppSettings.Default"/>; saves are atomic (write temp file, then move).
/// Đọc và ghi <see cref="AppSettings"/> dạng JSON tại một đường dẫn cố định (mặc định
/// <c>%LOCALAPPDATA%\ReTran\settings.json</c>). File thiếu hoặc hỏng trả về
/// <see cref="AppSettings.Default"/>; việc ghi là nguyên tử (ghi file tạm rồi move).
/// </summary>
/// <param name="path">File path, or <see langword="null"/> to use <see cref="DefaultPath"/>. Đường dẫn file, hoặc <see langword="null"/> để dùng <see cref="DefaultPath"/>.</param>
public sealed class AppSettingsService(string? path = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path = Path.GetFullPath(path ?? DefaultPath);
    private object PathLock => PathLocks.GetOrAdd(_path, static _ => new object());

    /// <summary>
    /// Raised after a successful <see cref="Save"/> or <see cref="Update"/> so open screens can
    /// refresh their copy of the settings (language pair, provider, overlay options).
    /// Được phát sau khi <see cref="Save"/> hoặc <see cref="Update"/> thành công để các màn hình
    /// đang mở đọc lại cài đặt (cặp ngôn ngữ, provider, tùy chọn overlay).
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Default settings file (%LOCALAPPDATA%\ReTran\settings.json).
    /// File cài đặt mặc định (%LOCALAPPDATA%\ReTran\settings.json).
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReTran", "settings.json");

    /// <summary>
    /// Reads the settings file under the per-path lock; a missing, malformed or incomplete file
    /// returns <see cref="AppSettings.Default"/> instead of throwing.
    /// Đọc file cài đặt dưới lock theo đường dẫn; file thiếu, hỏng hoặc thiếu trường trả về
    /// <see cref="AppSettings.Default"/> thay vì ném exception.
    /// </summary>
    /// <returns>The persisted settings, or the defaults when the file cannot be used. Cài đặt đã lưu, hoặc mặc định khi không dùng được file.</returns>
    public AppSettings Load()
    {
        lock (PathLock)
        {
            return LoadCore();
        }
    }

    /// <summary>
    /// Writes the settings file atomically under the per-path lock, creating the parent directory
    /// when missing and cleaning up its unique temporary file after a failure.
    /// Ghi file cài đặt nguyên tử dưới lock theo đường dẫn, tạo thư mục cha nếu chưa có và dọn file
    /// tạm duy nhất sau khi ghi lỗi.
    /// </summary>
    /// <param name="s">Settings to persist. Cài đặt cần lưu.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="s"/> is null. Ném khi <paramref name="s"/> là null.</exception>
    /// <exception cref="IOException">Thrown when the temporary or destination file cannot be written or replaced. Ném khi không ghi hoặc thay thế được file tạm/đích.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the settings directory or file denies access. Ném khi thư mục hoặc file settings từ chối quyền truy cập.</exception>
    public void Save(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        lock (PathLock)
        {
            SaveCore(s);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Atomically reads, transforms and saves settings under one per-full-path lock shared by all
    /// service instances in this process, then returns the persisted value.
    /// Đọc, biến đổi và ghi settings nguyên tử dưới cùng một lock theo full path được mọi service
    /// instance trong process chia sẻ, rồi trả về giá trị đã lưu.
    /// </summary>
    /// <param name="update">Transformation applied to the latest settings. Phép biến đổi áp dụng trên settings mới nhất.</param>
    /// <returns>The transformed settings persisted to disk. Settings đã biến đổi và lưu xuống đĩa.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="update"/> or its result is null. Ném khi <paramref name="update"/> hoặc kết quả của nó là null.</exception>
    /// <exception cref="IOException">Thrown when the temporary or destination file cannot be written or replaced. Ném khi không ghi hoặc thay thế được file tạm/đích.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the settings directory or file denies access. Ném khi thư mục hoặc file settings từ chối quyền truy cập.</exception>
    public AppSettings Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppSettings updated;
        lock (PathLock)
        {
            updated = update(LoadCore());
            ArgumentNullException.ThrowIfNull(updated);
            SaveCore(updated);
        }
        Changed?.Invoke();
        return updated;
    }

    private AppSettings LoadCore()
    {
        try
        {
            if (!File.Exists(_path)) return AppSettings.Default;
            AppSettings? settings =
                JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), SerializerOptions);
            if (settings is null
                || string.IsNullOrEmpty(settings.Provider)
                || string.IsNullOrEmpty(settings.Src)
                || string.IsNullOrEmpty(settings.Dst))
            {
                return AppSettings.Default;
            }
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
    }

    private void SaveCore(AppSettings s)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, SerializerOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve the original write/replace failure; a later save uses a fresh temp path.
                // Giữ lỗi ghi/thay thế ban đầu; lần lưu sau dùng một đường dẫn temp mới.
            }
        }
    }
}
