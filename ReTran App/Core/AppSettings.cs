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
    /// Loads settings from disk, creating the file with defaults when it is missing.
    /// Nạp cài đặt từ đĩa; tạo tệp với giá trị mặc định khi tệp chưa tồn tại.
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

        string json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
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
