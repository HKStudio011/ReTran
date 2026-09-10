using ReTran_App.Core;

namespace ReTran_App
{
    public partial class MainPage : ContentPage
    {
        private CoreProcessClient? _core;

        public MainPage()
        {
            InitializeComponent();
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
                string candidate = Path.Combine(dir.FullName, "ReTran Core", "core-cs", "bin", "Debug", "net10.0-windows", "ReTran.Core.exe");
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
        }
    }
}
