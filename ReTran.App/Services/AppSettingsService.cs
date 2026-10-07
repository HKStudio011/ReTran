using System.Text.Json;

namespace ReTran_App.Services;

/// <summary>
/// Translation settings persisted in <c>settings.json</c>: provider, language pair and an
/// optional API key for providers that need one.
/// Cài đặt dịch thuật lưu trong <c>settings.json</c>: nhà cung cấp, cặp ngôn ngữ và API key tùy
/// chọn cho các nhà cung cấp cần key.
/// </summary>
/// <param name="Provider">Provider name, e.g. <c>google</c>. Tên nhà cung cấp, ví dụ <c>google</c>.</param>
/// <param name="Src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
/// <param name="Dst">Target language code. Mã ngôn ngữ đích.</param>
/// <param name="ApiKey">API key for providers that require one; <see langword="null"/> otherwise. API key cho nhà cung cấp yêu cầu; ngược lại <see langword="null"/>.</param>
public sealed record AppSettings(string Provider, string Src, string Dst, string? ApiKey)
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

    private readonly string _path = path ?? DefaultPath;

    /// <summary>
    /// Default settings file (%LOCALAPPDATA%\ReTran\settings.json).
    /// File cài đặt mặc định (%LOCALAPPDATA%\ReTran\settings.json).
    /// </summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReTran", "settings.json");

    /// <summary>
    /// Reads the settings file; a missing, malformed or incomplete file returns
    /// <see cref="AppSettings.Default"/> instead of throwing.
    /// Đọc file cài đặt; file thiếu, hỏng hoặc thiếu trường trả về <see cref="AppSettings.Default"/>
    /// thay vì ném exception.
    /// </summary>
    /// <returns>The persisted settings, or the defaults when the file cannot be used. Cài đặt đã lưu, hoặc mặc định khi không dùng được file.</returns>
    public AppSettings Load()
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

    /// <summary>
    /// Writes the settings file atomically, creating the parent directory when missing.
    /// Ghi file cài đặt theo kiểu nguyên tử, tạo thư mục cha nếu chưa có.
    /// </summary>
    /// <param name="s">Settings to persist. Cài đặt cần lưu.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="s"/> is null. Ném khi <paramref name="s"/> là null.</exception>
    public void Save(AppSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, SerializerOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
