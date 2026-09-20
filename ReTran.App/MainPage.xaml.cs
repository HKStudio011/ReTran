using Microsoft.Extensions.Logging;
using ReTran_App.Core;
using ReTran_App.Services;

namespace ReTran_App
{
    public partial class MainPage : ContentPage
    {
        private CoreProcessClient? _core;
        // Overlay feed server: single DI instance serving the DI OverlayState to OBS.
        // Started once with the app window, disposed on exit. DemoFeed itself is
        // started/stopped only by the control page buttons, never here.
        private OverlayFeedServer? _feedServer;
        private bool _feedServerStarted;

        public MainPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_feedServerStarted) return;
            _feedServerStarted = true;
            try
            {
                var services = IPlatformApplication.Current?.Services;
                _feedServer = (OverlayFeedServer?)services?.GetService(typeof(OverlayFeedServer));
                if (_feedServer is null) return;
                await _feedServer.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Best-effort: the control page shows Port == 0 as "not running".
                // Best-effort: trang điều khiển hiển thị Port == 0 là "chưa chạy".
                var logger = (ILogger<MainPage>?)IPlatformApplication.Current?.Services.GetService(typeof(ILogger<MainPage>));
                logger?.LogWarning(ex, "Overlay feed server failed to start; OBS URL will show as not running.");
                _feedServer = null;
            }
        }

        private async void OnStartCoreClicked(object? sender, EventArgs e)
        {
            if (_core is not null) return;
            try
            {
                string exe = FindCoreExe();
                _core = new CoreProcessClient();
                await _core.StartAsync(exe, CancellationToken.None);
                StatusLabel.Text = "Core: started";
                PingButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                StartCoreButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Core not found / failed to start: {ex.Message}";
                StartCoreButton.IsEnabled = true; // allow retry after a failed start
            }
        }

        private async void OnPingClicked(object? sender, EventArgs e)
        {
            if (_core is null) return;
            try
            {
                var result = await _core.PingAsync(CancellationToken.None);
                StatusLabel.Text = $"Ping: {result}";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Ping failed: {ex.Message}";
            }
        }

        private async void OnStopClicked(object? sender, EventArgs e)
        {
            if (_core is null) return;
            await _core.DisposeAsync();
            _core = null;
            StatusLabel.Text = "Core: stopped";
            PingButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StartCoreButton.IsEnabled = true;
        }

        /// <summary>
        /// Resolves ReTran.Core.exe: RETRAN_CORE_EXE env var wins, otherwise walk up from the app base dir
        /// to the repo root and use the default build output path.
        /// Định vị ReTran.Core.exe: biến môi trường RETRAN_CORE_EXE được ưu tiên, nếu không thì đi lên từ thư mục
        /// gốc của app tới repo root và dùng đường dẫn build mặc định.
        /// </summary>
        private static string FindCoreExe()
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
            throw new FileNotFoundException("ReTran.Core.exe not found. Build it first or set RETRAN_CORE_EXE.");
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (_core is not null)
            {
                _core.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _core = null;
            }
            if (_feedServer is not null)
            {
                _feedServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _feedServer = null;
            }
        }
    }
}
