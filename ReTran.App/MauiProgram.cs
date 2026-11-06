using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.OverlayFeed;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Tray;
using ReTran_App.Services.Update;
using ReTran_App.Services.Window;

namespace ReTran_App
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                });

            builder.Services.AddMauiBlazorWebView();

            builder.Services.AddSingleton<JsInteropService>();
            builder.Services.AddSingleton<I18nService>();
            builder.Services.AddSingleton<ToastService>();
            builder.Services.AddSingleton<OverlayState>();
            builder.Services.AddSingleton<PreviewState>();

            // OBS overlay feed: loopback server serving the transparent /overlay page + SSE /events.
            // Feed overlay OBS: server loopback phục vụ trang trong suốt /overlay + SSE /events.
            // Một công tắc OverlayOnGame điều khiển cả overlay desktop lẫn feed OBS (spec K8): gate
            // phải là singleton dùng chung, nếu không hai bề mặt sẽ thấy hai giá trị khác nhau.
            // One OverlayOnGame switch drives both the desktop overlay and the OBS feed (spec K8): the
            // gate must be one shared singleton, or the two surfaces would see different values.
            builder.Services.AddSingleton<IOverlayVisibility, OverlayVisibility>();
            builder.Services.AddSingleton<OverlayFeedServer>();

            // Local game overlay: layered Win32 window + window/monitor anchor tracker.
            // Overlay game local: cửa sổ layered Win32 + tracker neo theo cửa sổ/monitor.
            builder.Services.AddSingleton<IOverlayHost, Win32OverlayHost>();
            builder.Services.AddSingleton<IAnchorTracker, Win32AnchorTracker>();
            builder.Services.AddSingleton<IForegroundWatcher, Win32ForegroundWatcher>();
            builder.Services.AddSingleton<OverlayWindowService>();

            // System tray: close-to-tray window bridge, Shell_NotifyIcon host, clean exit path,
            // error routing (balloon vs toast) and the menu actions behind it. The icon is only
            // registered from the primary-window hook (below).
            // Khay hệ thống: bridge cửa sổ close-to-tray, host Shell_NotifyIcon, đường thoát sạch,
            // định tuyến lỗi (balloon vs toast) và các hành động menu đứng sau đó. Icon chỉ được
            // đăng ký từ hook cửa sổ chính (bên dưới).
            builder.Services.AddSingleton<AppWindowBridge>();
            builder.Services.AddSingleton<ITrayHost, Win32TrayHost>();
            builder.Services.AddSingleton<ITrayExit, TrayExitService>();
            builder.Services.AddSingleton(sp => new AppNotifier(
                sp.GetRequiredService<ToastService>(),
                sp.GetRequiredService<ITrayHost>(),
                () => sp.GetRequiredService<AppWindowBridge>().IsVisible,
                () => sp.GetRequiredService<AppWindowBridge>().IsForeground,
                sp.GetRequiredService<ILogger<AppNotifier>>()));
            builder.Services.AddSingleton<ITrayActions, AppTrayActions>();
            builder.Services.AddSingleton<TrayIconService>();

            string dbPath = TranslationCache.DefaultDbPath;
            builder.Services.AddDbContext<RetranDbContext>(
                options => options.UseSqlite($"Data Source={dbPath}"),
                ServiceLifetime.Singleton,
                ServiceLifetime.Singleton);
            builder.Services.AddSingleton<AppSettingsService>();
            builder.Services.AddSingleton<CoreProcessClient>();
            builder.Services.AddSingleton<ICorePeer>(sp => sp.GetRequiredService<CoreProcessClient>());
            builder.Services.AddSingleton<TranslationCache>();
            builder.Services.AddSingleton<PipelineController>();

            builder.Services.AddSingleton(_ => new HttpClient());
            builder.Services.AddSingleton<GitHubAppUpdateService>();
            builder.Services.AddSingleton<IAppUpdateService>(sp => sp.GetRequiredService<GitHubAppUpdateService>());
            builder.Services.AddSingleton<UpdateStopper>();
            builder.Services.AddSingleton<IUpdateStopper>(sp => sp.GetRequiredService<UpdateStopper>());
            builder.Services.AddSingleton<UpdateCoordinator>();
            builder.Services.AddSingleton<UpdateCheckService>();
            builder.Services.AddSingleton<IAppShutdown, MauiAppShutdown>();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            // Hook the primary window: pressing X hides it to the tray, and the tray icon is
            // registered on the UI thread — exactly where the host's message-only window is created
            // (no new thread, no timer). Only ordinal 0 is hooked (see WindowHooks): a second
            // window would mean a second icon and a double hide. The counter starts at -1 because
            // Interlocked.Increment hands 0 to the first window.
            // Hook cửa sổ chính: bấm X ẩn cửa sổ xuống khay, và icon khay được đăng ký trên UI
            // thread — đúng nơi cửa sổ message-only của host được tạo (không thread mới, không
            // timer). Chỉ ordinal 0 được hook (xem WindowHooks): window thứ hai sẽ sinh icon thứ hai
            // và ẩn hai lần. Bộ đếm bắt đầu ở -1 vì Interlocked.Increment trả 0 cho window đầu tiên.
            int windowOrdinal = -1;
            builder.ConfigureLifecycleEvents(events => events.AddWindows(w => w.OnWindowCreated(window =>
            {
                var ordinal = Interlocked.Increment(ref windowOrdinal);
                if (!WindowHooks.ShouldHookWindow(ordinal)) return; // chỉ window chính (Review Focus #5)

                try
                {
                    IServiceProvider? services = IPlatformApplication.Current?.Services; // pattern repo, xem App.xaml.cs:10
                    if (services is null) return;
                    services.GetRequiredService<AppWindowBridge>().Attach(window);
                    services.GetRequiredService<TrayIconService>().Start();
                }
                catch (Exception ex)
                {
                    // Khởi động phải sống sót khi hook tray hỏng: tray là tiện ích, app mới là thứ
                    // được bảo vệ. Attach kích hoạt WinRT (GetWindowHandle/GetWindowIdFromWindow)
                    // có thể ném lỗi, và Start cũng vậy — không gì ở nhánh này được giết app.
                    // Startup must survive a failed tray hook: the tray is a convenience, the app is
                    // not. Attach activates WinRT (GetWindowHandle/GetWindowIdFromWindow) and can
                    // throw, and so can Start — nothing in this branch may kill the app.
                    IPlatformApplication.Current?.Services?
                        .GetService<ILoggerFactory>()?
                        .CreateLogger("ReTran.App.Tray")
                        .LogError(ex, "The tray/close-to-tray hook failed on window creation; the app continues without it.");
                }
            })));

            MauiApp app = builder.Build();
            MigrateDatabase(app.Services, dbPath);
            return app;
        }

        private static void MigrateDatabase(IServiceProvider services, string dbPath)
        {
            try
            {
                // TranslationCache.DefaultDbPath does not create the parent directory.
                string? dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                services.GetRequiredService<RetranDbContext>().Database.Migrate();
            }
            catch (Exception ex)
            {
                services.GetService<ILoggerFactory>()?
                    .CreateLogger("ReTran.App.Startup")
                    .LogError(ex, "Database migration failed at startup; the translation cache is unavailable.");
            }
        }
    }
}
