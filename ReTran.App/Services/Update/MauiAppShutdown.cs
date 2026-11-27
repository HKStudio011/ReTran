namespace ReTran_App.Services.Update;

/// <summary>
/// MAUI implementation of <see cref="IAppShutdown"/> — quits the current application after updater
/// launch or terminal recovery (spec §7 step 5).
/// Hiện thực MAUI của <see cref="IAppShutdown"/> — đóng ứng dụng hiện tại sau khi khởi chạy updater
/// hoặc khi recovery kết thúc (spec §7 bước 5).
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
