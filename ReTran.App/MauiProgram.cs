using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;

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
            builder.Services.AddSingleton<OverlayState>();
            builder.Services.AddSingleton<OverlayFeedServer>();

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
