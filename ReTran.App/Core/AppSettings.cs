using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReTran_App.Core;

/// <summary>
/// Application settings persisted at %LOCALAPPDATA%\ReTran\settings.json.
/// Cài đặt ứng dụng được lưu tại %LOCALAPPDATA%\ReTran\settings.json.
/// </summary>
public sealed record AppSettings(
    string? CoreExePath = null,
    [property: JsonPropertyName("pythonExe")] string PythonExe = "python")
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// Settings file location: %LOCALAPPDATA%\ReTran\settings.json.
    /// Vị trí tệp cài đặt: %LOCALAPPDATA%\ReTran\settings.json.
    /// </summary>
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ReTran", "settings.json");

    /// <summary>
    /// Loads settings from disk, returning defaults when the file is absent or malformed;
    /// creates the file with defaults on first run.
    /// Nạp cài đặt từ đĩa; trả về giá trị mặc định khi tệp không tồn tại hoặc sai định dạng;
    /// tạo tệp với giá trị mặc định ở lần chạy đầu.
    /// </summary>
    public static async Task<AppSettings> LoadAsync()
    {
        string path = SettingsPath;
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults);
            return defaults;
        }

        try
        {
            string json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            // The file may vanish between the check and the read, or be malformed: fall back to defaults.
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings to disk, creating the containing directory if needed.
    /// Lưu cài đặt ra đĩa; tạo thư mục chứa nếu cần.
    /// </summary>
    public static async Task SaveAsync(AppSettings settings)
    {
        string path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(settings, Options);
        await File.WriteAllTextAsync(path, json);
    }
}
