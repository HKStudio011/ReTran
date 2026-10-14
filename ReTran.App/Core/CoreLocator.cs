namespace ReTran_App.Core;

/// <summary>
/// Resolves the ReTran.Core.exe path shared by MainPage and the Appium smoke test.
/// Tìm đường dẫn ReTran.Core.exe dùng chung cho MainPage và smoke test Appium.
/// </summary>
public static class CoreLocator
{
    /// <summary>
    /// Returns the file named by RETRAN_CORE_EXE when it exists; otherwise the sibling
    /// <c>ReTran.Core.exe</c> next to the app (the self-contained publish layout); otherwise walks
    /// up from the app base directory to the repo root and returns the default Debug build path.
    /// Null when nothing is found — never throws.
    /// Trả về tệp do RETRAN_CORE_EXE chỉ tới khi tồn tại; nếu không thì tệp
    /// <c>ReTran.Core.exe</c> cạnh app (layout publish self-contained); nếu vẫn không thì đi lên
    /// từ thư mục gốc app tới repo root và trả về đường dẫn build Debug mặc định.
    /// Null khi không tìm thấy — không bao giờ ném lỗi.
    /// </summary>
    public static string? FindCoreExe()
        => FindCoreExe(Environment.GetEnvironmentVariable("RETRAN_CORE_EXE"), AppContext.BaseDirectory);

    /// <summary>
    /// Testable core of <see cref="FindCoreExe"/>: env value → sibling <c>ReTran.Core.exe</c> in
    /// <paramref name="baseDirectory"/> → repo-walk Debug fallback.
    /// Phần lõi testable của <see cref="FindCoreExe"/>: giá trị env → <c>ReTran.Core.exe</c> cạnh
    /// <paramref name="baseDirectory"/> → fallback Debug đi lên repo root.
    /// </summary>
    /// <param name="envValue">Value of RETRAN_CORE_EXE, or <see langword="null"/>/empty when unset. Giá trị RETRAN_CORE_EXE, hoặc <see langword="null"/>/rỗng khi chưa đặt.</param>
    /// <param name="baseDirectory">Directory the sibling check starts from. Thư mục bắt đầu kiểm tra sibling.</param>
    /// <returns>An existing file path, or <see langword="null"/> when nothing was found. Đường dẫn file đã tồn tại, hoặc <see langword="null"/> khi không tìm thấy.</returns>
    internal static string? FindCoreExe(string? envValue, string baseDirectory)
    {
        if (!string.IsNullOrEmpty(envValue) && File.Exists(envValue)) return envValue;

        string sibling = Path.Combine(baseDirectory, "ReTran.Core.exe");
        if (File.Exists(sibling)) return sibling;

        var dir = new DirectoryInfo(baseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ReTran.Core", "bin", "Debug", "net10.0-windows", "ReTran.Core.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
