namespace ReTran_App.Services.Setup;

/// <summary>
/// Fixed on-disk layout the first-run setup wizard works with: the OCR source tree the build copies
/// beside the app, its virtualenv and model cache under that tree, the bundled uv executable, and the
/// wizard state file in <c>%LOCALAPPDATA%\ReTran</c>.
/// Bố cục cố định trên đĩa mà wizard chạy lần đầu làm việc cùng: cây source OCR do build copy cạnh
/// app, virtualenv và cache model của nó nằm dưới cây đó, uv đi kèm, và file trạng thái wizard trong
/// <c>%LOCALAPPDATA%\ReTran</c>.
/// </summary>
public static class SetupPaths
{
    /// <summary>
    /// Directory the running app lives in, without a trailing separator.
    /// Thư mục của app đang chạy, không có separator ở cuối.
    /// </summary>
    public static string AppDir { get; } = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>
    /// OCR sidecar source tree copied next to the app by the build.
    /// Cây source sidecar OCR được build copy ngay cạnh app.
    /// </summary>
    public static string OcrDir => Path.Combine(AppDir, "ReTran.OCR");

    /// <summary>
    /// Python interpreter inside the sidecar's uv-managed virtualenv; missing until <c>uv sync</c> ran.
    /// Interpreter Python trong virtualenv do uv quản lý của sidecar; chưa tồn tại cho tới khi <c>uv sync</c> chạy.
    /// </summary>
    public static string VenvPythonExe => Path.Combine(OcrDir, ".venv", "Scripts", "python.exe");

    /// <summary>
    /// OCR model cache root (<c>PADDLE_PDX_CACHE_HOME</c>); downloaded models land under
    /// <c>official_models\&lt;name&gt;</c> inside it.
    /// Thư mục gốc cache model OCR (<c>PADDLE_PDX_CACHE_HOME</c>); model tải về nằm trong
    /// <c>official_models\&lt;tên&gt;</c> bên trong nó.
    /// </summary>
    public static string ModelsDir => Path.Combine(OcrDir, "models");

    /// <summary>
    /// Bundled uv executable shipped under <c>runtimes\win-x64</c>.
    /// uv đi kèm, đóng gói dưới <c>runtimes\win-x64</c>.
    /// </summary>
    public static string UvExe => Path.Combine(AppDir, "runtimes", "win-x64", "uv.exe");

    /// <summary>
    /// Wizard state file (%LOCALAPPDATA%\ReTran\setup.json).
    /// File trạng thái wizard (%LOCALAPPDATA%\ReTran\setup.json).
    /// </summary>
    public static string SetupJsonPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReTran", "setup.json");
}
