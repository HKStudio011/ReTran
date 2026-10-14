namespace ReTran_App.Services.Update;

/// <summary>
/// Compile-time gate for the whole update subsystem.
/// Cổng bật/tắt ở mức compile cho toàn bộ hệ thống update.
/// </summary>
/// <remarks>
/// The installer replaces the running installation (ReTran.Updater.exe shadows the files under
/// <c>%LOCALAPPDATA%\ReTran_Update</c>). A DEBUG build runs straight out of the repository's
/// <c>bin\Debug</c> output folder, so letting an update apply during development would overwrite the
/// developer's own build artifacts with a downloaded package. Turning the flag off in DEBUG stops the
/// background loop from ever starting there; Release builds keep the normal Windows-only behaviour.
/// Bộ cài thay thế bản đang chạy (ReTran.Updater.exe shadow các file dưới
/// <c>%LOCALAPPDATA%\ReTran_Update</c>). Bản DEBUG chạy thẳng từ thư mục output <c>bin\Debug</c> của
/// repo, nên nếu cho update áp dụng lúc dev sẽ đè lên artifact build của chính dev bằng package tải
/// về. Tắt cờ ở DEBUG để vòng lặp nền không bao giờ khởi động ở đó; bản Release giữ hành vi
/// Windows-only như bình thường.
/// </remarks>
public static class UpdateFeature
{
    /// <summary>
    /// Whether the update subsystem may run — <see langword="false"/> in DEBUG builds, Windows-only otherwise.
    /// Hệ thống update có được phép chạy hay không — <see langword="false"/> ở bản DEBUG, ngoài ra chỉ trên Windows.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
#if DEBUG
            return false;
#else
            return OperatingSystem.IsWindows();
#endif
        }
    }
}