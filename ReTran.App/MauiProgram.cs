using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Update;

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
            builder.Services.AddSingleton<OverlayFeedServer>();
            builder.Services.AddSingleton<IOverlayFeedServer>(sp => sp.GetRequiredService<OverlayFeedServer>());

            // Local game overlay: layered Win32 window + window/monitor anchor tracker.
            // Overlay game local: cửa sổ layered Win32 + tracker neo theo cửa sổ/monitor.
            builder.Services.AddSingleton<IOverlayHost, Win32OverlayHost>();
            builder.Services.AddSingleton<IAnchorTracker, Win32AnchorTracker>();
            builder.Services.AddSingleton<OverlayWindowService>();

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
