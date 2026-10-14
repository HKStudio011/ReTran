namespace ReTran_App.Services.Update;

/// <summary>
/// MAUI implementation of <see cref="IAppShutdown"/> — quits the current application so the
/// already-launched updater can replace the install files (spec §7 step 5).
/// Hiện thực MAUI của <see cref="IAppShutdown"/> — đóng ứng dụng hiện tại để updater đã được
/// khởi chạy có thể thay các file cài (spec §7 bước 5).
/// </summary>
public sealed class MauiAppShutdown : IAppShutdown
{
    /// <summary>
    /// Quits the current application; no-op when no application instance exists yet
    /// (e.g. during startup failures or unit tests).
    /// Đóng ứng dụng hiện tại; noop khi chưa có instance ứng dụng nào
    /// (ví dụ lúc startup lỗi hoặc trong unit test).
    /// </summary>
    public void Quit() => Application.Current?.Quit();
}
