using Microsoft.Extensions.Logging;

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
            builder.Services.AddSingleton<ReTran_App.Services.JsInteropService>();
            // Task 2 will provide OverlayState — uncomment then.
            // OverlayState sẽ được tạo ở Task 2 — mở comment dòng dưới khi đó.
            // builder.Services.AddSingleton<ReTran_App.Core.OverlayState>();

#if DEBUG
    		builder.Services.AddBlazorWebViewDeveloperTools();
    		builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
