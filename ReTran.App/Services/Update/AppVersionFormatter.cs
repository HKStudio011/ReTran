namespace ReTran_App.Services.Update;

/// <summary>
/// Formats a <see cref="Version"/> as a short string for UI display (footer/Settings):
/// always at least Major.Minor, trailing zeros trimmed.
/// Định dạng <see cref="Version"/> thành chuỗi ngắn để hiển thị trên UI (footer/Settings):
/// luôn tối thiểu Major.Minor, cắt các số 0 ở cuối.
/// </summary>
public static class AppVersionFormatter
{
    /// <summary>
    /// Parses a version string and formats it in short form — convenience for
    /// <c>IAppUpdateService.CurrentVersion</c> (the UI never needs to call <c>Version.Parse</c> itself).
    /// Parse chuỗi version rồi định dạng dạng ngắn — convenience cho
    /// <c>IAppUpdateService.CurrentVersion</c> (UI không cần tự <c>Version.Parse</c>).
    /// </summary>
    /// <param name="version">
    /// A version string valid per <see cref="Version.Parse(string)"/> (e.g. "1.0.3.0").
    /// Chuỗi version hợp lệ theo <see cref="Version.Parse(string)"/> (vd. "1.0.3.0").
    /// </param>
    /// <returns>
    /// The short version string, at least two parts (Major.Minor).
    /// Chuỗi version dạng ngắn, tối thiểu 2 phần (Major.Minor).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is null. <paramref name="version"/> là null.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> cannot be parsed into a <see cref="Version"/>. <paramref name="version"/> không parse được thành <see cref="Version"/>.</exception>
    public static string Format(string version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return Format(Version.Parse(version));
    }

    /// <summary>
    /// Converts a version to short form: 1.0.0.0 → "1.0", 1.0.3.0 → "1.0.3", 1.0.0.4 → "1.0.0.4".
    /// Chuyển version sang dạng ngắn: 1.0.0.0 → "1.0", 1.0.3.0 → "1.0.3", 1.0.0.4 → "1.0.0.4".
    /// </summary>
    /// <param name="version">The version to format; null is not accepted. Version cần định dạng; không nhận null.</param>
    /// <returns>
    /// The short version string, at least two parts (Major.Minor).
    /// Chuỗi version dạng ngắn, tối thiểu 2 phần (Major.Minor).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="version"/> is null. <paramref name="version"/> là null.</exception>
    public static string Format(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Version 2/3 thành phần cho Build/Revision = -1 (undefined) — chỉ lấy component có thật.
        var parts = new List<int> { version.Major, version.Minor };
        if (version.Build >= 0)
        {
            parts.Add(version.Build);
        }

        if (version.Revision >= 0)
        {
            parts.Add(version.Revision);
        }

        // Cắt số 0 cuối nhưng giữ tối thiểu Major.Minor — 1.0.0.0 → "1.0", 1.0.3.0 → "1.0.3".
        var end = parts.Count;
        while (end > 2 && parts[end - 1] == 0)
        {
            end--;
        }

        return string.Join('.', parts.Take(end));
    }
}
