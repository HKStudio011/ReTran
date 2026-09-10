using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReTran.Core.Config;

/// <summary>
/// Loads core configuration from a file. M0 uses JSON (see <see cref="JsonConfigLoader"/>);
/// the loader is deliberately hidden behind this interface so YAML can be swapped in later
/// without touching any caller of <see cref="CoreConfig.LoadFrom"/>.
/// Đọc cấu hình lõi từ tệp. M0 dùng JSON (xem <see cref="JsonConfigLoader"/>); bộ nạp được
/// giấu sau giao diện này để có thể thay bằng YAML về sau mà không đụng tới nơi gọi
/// <see cref="CoreConfig.LoadFrom"/>.
/// </summary>
public interface ICoreConfigLoader
{
    /// <summary>
    /// Loads configuration from the given path, returning defaults when the file is absent.
    /// Nạp cấu hình từ đường dẫn cho trước; trả về giá trị mặc định khi tệp không tồn tại.
    /// </summary>
    CoreConfig Load(string path);
}

/// <summary>
/// JSON implementation of <see cref="ICoreConfigLoader"/> for M0.
/// Implement JSON của <see cref="ICoreConfigLoader"/> cho M0.
/// </summary>
public sealed class JsonConfigLoader : ICoreConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public CoreConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CoreConfig();
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CoreConfig>(json, Options) ?? new CoreConfig();
    }
}

/// <summary>
/// Core process configuration (OCR rate, Python interpreter path, ...).
/// Cấu hình tiến trình lõi (tốc độ OCR, đường dẫn trình thông dịch Python, ...).
/// </summary>
public sealed record CoreConfig(
    string? PythonExe = null,
    [property: JsonPropertyName("ocrFps")] int OcrFps = 3)
{
    private static readonly ICoreConfigLoader Loader = new JsonConfigLoader();

    /// <summary>
    /// Loads core configuration from the given path, returning defaults when the file is absent.
    /// Nạp cấu hình lõi từ đường dẫn cho trước; trả về giá trị mặc định khi tệp không tồn tại.
    /// </summary>
    public static CoreConfig LoadFrom(string path) => Loader.Load(path);

    /// <summary>
    /// Default config file location: %LOCALAPPDATA%\ReTran\core\config.json.
    /// Vị trí tệp cấu hình mặc định: %LOCALAPPDATA%\ReTran\core\config.json.
    /// </summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReTran", "core", "config.json");
}
