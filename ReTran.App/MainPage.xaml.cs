using Microsoft.Extensions.Logging;
using ReTran_App.Core;
using ReTran_App.Services;

namespace ReTran_App
{
    public partial class MainPage : ContentPage
    {
        private ICorePeer? _core;
        // Overlay feed server: single DI instance serving the DI OverlayState to OBS.
        // Started once with the app window, disposed on exit. DemoFeed itself is
        // started/stopped only by the control page buttons, never here.
        private OverlayFeedServer? _feedServer;
        private bool _feedServerStarted;
        private I18nService? _l10n;
        private string? _statusKind; // "stopped" | "started" | "startFailed" | "raw"
        private string? _statusArg;

        public MainPage()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            if (_feedServerStarted) return;
            _feedServerStarted = true;

            var services = IPlatformApplication.Current?.Services;
            _l10n = (I18nService?)services?.GetService(typeof(I18nService));
            if (_l10n is not null)
            {
                _l10n.Changed += OnLangChanged;
                ApplyLocalizedTexts();
            }

            try
            {
                _feedServer = (OverlayFeedServer?)services?.GetService(typeof(OverlayFeedServer));
                if (_feedServer is null) return;
                await _feedServer.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Best-effort: the control page shows Port == 0 as "not running".
                // Best-effort: trang điều khiển hiển thị Port == 0 là "chưa chạy".
                var logger = (ILogger<MainPage>?)services?.GetService(typeof(ILogger<MainPage>));
                logger?.LogWarning(ex, "Overlay feed server failed to start; OBS URL will show as not running.");
                _feedServer = null;
            }
        }

        // I18nService.Set có thể chạy trên thread WebView → marshal về UI thread của MAUI.
        private void OnLangChanged() => Dispatcher.Dispatch(ApplyLocalizedTexts);

        private void ApplyLocalizedTexts()
        {
            if (_l10n is null) return;
            StartCoreButton.Text = _l10n["core.btn.startCore"];
            _statusKind ??= "stopped";
            if (_statusKind is "stopped" or "started" or "startFailed")
            {
                StatusLabel.Text = RenderStatusText();
            }
        }

        private void SetStatus(string kind, string? arg = null)
        {
            _statusKind = kind;
            _statusArg = arg;
            StatusLabel.Text = RenderStatusText();
        }

        private string RenderStatusText()
        {
            if (_l10n is null)
            {
                return _statusKind switch
                {
                    "stopped" => "Core: stopped",
                    "started" => "Core: started",
                    "startFailed" => $"Core not found / failed to start: {_statusArg}",
                    _ => _statusArg ?? StatusLabel.Text, // "raw": chuỗi technical (Ping) giữ nguyên
                };
            }
            return _statusKind switch
            {
                "stopped" => _l10n["core.status.stopped"],
                "started" => _l10n["core.status.started"],
                "startFailed" => _l10n.Fmt("core.status.startFailed", _statusArg ?? string.Empty),
                _ => _statusArg ?? StatusLabel.Text, // "raw": chuỗi technical (Ping) giữ nguyên
            };
        }

        private async void OnStartCoreClicked(object? sender, EventArgs e)
        {
            _core ??= IPlatformApplication.Current?.Services?.GetService(typeof(ICorePeer)) as ICorePeer;
            if (_core is null || _core.IsStarted) return;
            try
            {
                await _core.StartCoreAsync(CancellationToken.None);
                SetStatus("started");
                PingButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                StartCoreButton.IsEnabled = false;
            }
            catch (Exception ex)
            {
                SetStatus("startFailed", ex.Message);
                StartCoreButton.IsEnabled = true; // allow retry after a failed start
            }
        }

        private async void OnPingClicked(object? sender, EventArgs e)
        {
            if (_core is not { IsStarted: true }) return;
            try
            {
                var result = await _core.CallAsync("core.ping", null, CancellationToken.None);
                SetStatus("raw", $"Ping: {result}");
            }
            catch (Exception ex)
            {
                SetStatus("raw", $"Ping failed: {ex.Message}");
            }
        }

        private async void OnStopClicked(object? sender, EventArgs e)
        {
            if (_core is not { IsStarted: true }) return;
            // Dispose the DI singleton; the next start (page or pipeline) re-launches the core.
            if (_core is IAsyncDisposable disposable) await disposable.DisposeAsync();
            SetStatus("stopped");
            PingButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            StartCoreButton.IsEnabled = true;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (_core is IAsyncDisposable disposable)
            {
                disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            if (_feedServer is not null)
            {
                _feedServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                _feedServer = null;
            }
        }
    }
}
