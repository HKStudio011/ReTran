using Microsoft.Extensions.Logging;
using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Orchestrates the local game overlay: polls the capture source's on-screen rect, throttles
/// redraws of the latest <see cref="OverlayState"/> frame through <see cref="IOverlayHost"/>,
/// and persists the user's overlay options via <see cref="AppSettingsService"/>.
/// Điều phối overlay game local: poll hình chữ nhật màn hình của nguồn capture, throttle
/// vẽ lại frame mới nhất từ <see cref="OverlayState"/> qua <see cref="IOverlayHost"/>,
/// và lưu tùy chọn overlay của người dùng qua <see cref="AppSettingsService"/>.
/// </summary>
public sealed class OverlayWindowService : IDisposable
{
    private static readonly TimeSpan AnchorPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultRedrawInterval = TimeSpan.FromMilliseconds(66); // ≈15 fps

    private readonly OverlayState _overlay;
    private readonly PipelineController _pipeline;
    private readonly IOverlayHost _host;
    private readonly IAnchorTracker _anchor;
    private readonly AppSettingsService _settings;
    private readonly ILogger<OverlayWindowService> _log;
    private readonly TimeSpan _redrawInterval;
    private readonly object _gate = new();

    private Timer? _timer;
    private double _fontScale;
    private (int X, int Y, int Width, int Height)? _anchorRect;
    private DateTime _lastDrawUtc = DateTime.MinValue;
    private bool _enabled;
    private bool _disposed;

    /// <summary>
    /// Creates the service; call <see cref="SetEnabled"/> to start the overlay.
    /// Tạo service; gọi <see cref="SetEnabled"/> để bắt đầu overlay.
    /// </summary>
    /// <param name="overlay">Shared overlay frame state. Trạng thái khung overlay dùng chung.</param>
    /// <param name="pipeline">Pipeline controller supplying the active source and stop signals. Controller cung cấp nguồn đang hoạt động và tín hiệu dừng.</param>
    /// <param name="host">Overlay window implementation. Implementation cửa sổ overlay.</param>
    /// <param name="anchor">Source rectangle resolver. Trình xác định hình chữ nhật nguồn.</param>
    /// <param name="settings">Settings store for persistence. Nơi lưu cài đặt.</param>
    /// <param name="log">Logger. Bộ ghi log.</param>
    /// <param name="redrawInterval">Minimum time between redraws; <see langword="null"/> uses the ≈15 fps default. Khoảng thời gian tối thiểu giữa các lần vẽ; <see langword="null"/> dùng mặc định ≈15 fps.</param>
    public OverlayWindowService(
        OverlayState overlay,
        PipelineController pipeline,
        IOverlayHost host,
        IAnchorTracker anchor,
        AppSettingsService settings,
        ILogger<OverlayWindowService> log,
        TimeSpan? redrawInterval = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _overlay = overlay;
        _pipeline = pipeline;
        _host = host;
        _anchor = anchor;
        _settings = settings;
        _log = log;
        _redrawInterval = redrawInterval ?? DefaultRedrawInterval;
        _fontScale = LoadFontScale(_settings.Load());

        _overlay.Changed += OnFrameChanged;
        _pipeline.PipelineStateChanged += OnPipelineStateChanged;
    }

    /// <summary>
    /// Whether the overlay window is currently enabled. Overlay cửa sổ hiện đang bật hay không.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_gate) return _enabled; }
    }

    /// <summary>
    /// Enables (create window + start anchor polling) or disables (stop polling + destroy
    /// window) the overlay, persisting <c>OverlayOnGame</c> either way. Bật (tạo cửa sổ + bắt
    /// đầu poll anchor) hoặc tắt (dừng poll + hủy cửa sổ) overlay, lưu <c>OverlayOnGame</c> trong cả hai trường hợp.
    /// </summary>
    /// <param name="target">Desired state. Trạng thái mong muốn.</param>
    public void SetEnabled(bool target)
    {
        lock (_gate)
        {
            if (_disposed || _enabled == target) return;
            _enabled = target;

            if (target)
            {
                _host.Create();
                _timer = new Timer(static self => ((OverlayWindowService)self!).TickAnchor(), this,
                    AnchorPollInterval, AnchorPollInterval);
            }
            else
            {
                _timer?.Dispose();
                _timer = null;
                _anchorRect = null;
                _host.Dispose();
            }

            Persist(s => s with { OverlayOnGame = target });
        }
    }

    /// <summary>
    /// Applies the overlay font scale and persists it; an invalid value keeps the previous scale
    /// and logs a warning. Áp dụng hệ số cỡ chữ overlay và lưu lại; giá trị không hợp lệ giữ hệ số
    /// cũ và ghi log warning.
    /// </summary>
    /// <param name="fontScale">Font multiplier in (0, 4]; <see langword="null"/> keeps the current scale. Hệ số cỡ chữ trong (0, 4]; <see langword="null"/> giữ hệ số hiện tại.</param>
    public void ApplyOptions(double? fontScale = null)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (fontScale is not null)
            {
                if (double.IsFinite(fontScale.Value) && fontScale.Value is > 0 and <= 4) _fontScale = fontScale.Value;
                else _log.LogWarning("Ignoring invalid overlay font scale {FontScale}.", fontScale);
            }

            Persist(s => s with { OverlayFontScale = _fontScale });
        }
    }

    /// <summary>
    /// Re-checks the active source's on-screen rect: hides the overlay when the source is
    /// gone or minimized, shows/moves it when the rect changed. Called by the internal
    /// poll timer; exposed for tests. Kiểm tra lại hình chữ nhật màn hình của nguồn đang
    /// hoạt động: ẩn overlay khi nguồn biến mất hoặc thu nhỏ, hiện/di chuyển khi rect đổi.
    /// Được gọi bởi timer poll nội bộ; expose cho test.
    /// </summary>
    public void TickAnchor()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return;

            SourceSelection? source = _pipeline.ActiveSource;
            (int X, int Y, int Width, int Height)? rect = source is null ? null : _anchor.GetAnchorRect(source);
            if (rect is null)
            {
                _anchorRect = null;
                _host.Hide();
                return;
            }
            if (rect == _anchorRect) return;

            _anchorRect = rect;
            _host.Show(rect.Value.X, rect.Value.Y, rect.Value.Width, rect.Value.Height);
        }
    }

    /// <summary>
    /// Stops polling, unsubscribes events and disposes the host window.
    /// Dừng poll, hủy đăng ký event và dispose cửa sổ host.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            _overlay.Changed -= OnFrameChanged;
            _pipeline.PipelineStateChanged -= OnPipelineStateChanged;
            _timer?.Dispose();
            _timer = null;
            _host.Dispose();
        }
    }

    private void OnFrameChanged()
    {
        lock (_gate)
        {
            if (_disposed || !_enabled) return;
            if (_anchorRect is null) return;

            DateTime now = DateTime.UtcNow;
            if (now - _lastDrawUtc < _redrawInterval) return;

            OverlayFrame? frame = _overlay.Latest;
            if (frame is null) return;

            (int X, int Y, int Width, int Height) anchor = _anchorRect.Value;
            IReadOnlyList<DrawBox> boxes = OverlayLayout.Render(frame, anchor.Width, anchor.Height);
            _lastDrawUtc = now;
            _host.Draw(boxes, _fontScale);
        }
    }

    private void OnPipelineStateChanged(bool running, string? reason)
    {
        lock (_gate)
        {
            if (_disposed || !_enabled || running) return;
            _anchorRect = null;
            _host.Hide();
        }
    }

    private void Persist(Func<AppSettings, AppSettings> update)
    {
        try
        {
            _settings.Save(update(_settings.Load()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not persist overlay settings.");
        }
    }

    private static double LoadFontScale(AppSettings s) =>
        double.IsFinite(s.OverlayFontScale) && s.OverlayFontScale is > 0 and <= 4 ? s.OverlayFontScale : 1.0;
}
