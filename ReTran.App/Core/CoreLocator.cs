namespace ReTran_App.Core;

/// <summary>
/// Resolves the ReTran.Core.exe path shared by MainPage and the Appium smoke test.
/// Tìm đường dẫn ReTran.Core.exe dùng chung cho MainPage và smoke test Appium.
/// </summary>
public static class CoreLocator
{
    /// <summary>
    /// Returns the file named by RETRAN_CORE_EXE when it exists; otherwise walks up from the app base
    /// directory to the repo root and returns the default Debug build path. Null when nothing is found.
    /// Trả về tệp do RETRAN_CORE_EXE chỉ tới khi tồn tại; nếu không thì đi lên từ thư mục gốc app
    /// tới repo root và trả về đường dẫn build Debug mặc định. Null khi không tìm thấy.
    /// </summary>
    public static string? FindCoreExe()
    {
        string? env = Environment.GetEnvironmentVariable("RETRAN_CORE_EXE");
        if (env is not null && File.Exists(env)) return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "ReTran.Core", "bin", "Debug", "net10.0-windows", "ReTran.Core.exe");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
