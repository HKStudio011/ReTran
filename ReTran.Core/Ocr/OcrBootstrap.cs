using ReTran.Core.Config;

namespace ReTran.Core.Ocr;

/// <summary>
/// Resolves the repo root and Python interpreter shared by the ocr CLI and serve mode.
/// Tìm thư mục gốc repo và trình thông dịch Python dùng chung cho CLI ocr và serve.
/// </summary>
public static class OcrBootstrap
{
    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for ReTran.OCR/pyproject.toml.
    /// Đi ngược lên từ <paramref name="startDir"/> tìm ReTran.OCR/pyproject.toml.
    /// </summary>
    /// <returns>The repo root, or null when not inside the repo layout. Gốc repo, hoặc null khi không nằm trong layout repo.</returns>
    public static string? FindRepoRoot(string? startDir = null)
    {
        var d = new DirectoryInfo(startDir ?? Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ReTran.OCR", "pyproject.toml")))
            d = d.Parent;
        return d?.FullName;
    }

    /// <summary>
    /// Resolves the sidecar Python: config PythonExe when it exists, else the uv venv under repoRoot.
    /// Tìm Python cho sidecar: config PythonExe khi tồn tại, không thì venv uv trong repoRoot.
    /// </summary>
    /// <returns>Full path to python.exe, or null when nothing usable exists. Đường dẫn python.exe đầy đủ, hoặc null khi không có.</returns>
    public static string? ResolvePythonExe(CoreConfig config, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(repoRoot);
        if (!string.IsNullOrWhiteSpace(config.PythonExe) && File.Exists(config.PythonExe))
            return config.PythonExe;
        string venv = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        return File.Exists(venv) ? venv : null;
    }
}
