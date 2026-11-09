using System.Drawing;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Services;
using ReTran_App.Services.FloatingMenu;
using ReTran_App.Services.OverlayWindow;
using ReTran_App.Services.Tray;

public class FloatingMenuServiceTests
{
    /// <summary>
    /// Host fake recording every window call so tests can assert the show/hide/draw sequence and
    /// the bitmap sizes pushed. Fake host ghi log mọi lời gọi window để test assert chuỗi
    /// show/hide/draw và kích thước bitmap đã đẩy.
    /// </summary>
    private sealed class FakeMenuHost : IMenuWindowHost
    {
        public int CreateMenuCalls { get; private set; }
        public List<(int X, int Y, int W, int H)> ShowCalls { get; } = new();
        public int HideCalls { get; private set; }
        public List<Bitmap> DrawnBitmaps { get; } = new();

        public bool MenuCreated => CreateMenuCalls > 0;
        public nint MenuHwnd => 0;

        public void CreateMenu() => CreateMenuCalls++;
        public void ShowMenu(int x, int y, int width, int height) => ShowCalls.Add((x, y, width, height));
        public void HideMenu() => HideCalls++;
        public void DrawMenu(Bitmap bitmap) => DrawnBitmaps.Add(bitmap);

        // The service does not subscribe to the host events itself (wiring is Task 23); tests call
        // OnPanelClicked/OnDragFinished directly, so these accessors stay empty (avoids CS0067).
        // Service tự KHÔNG đăng ký event host (việc nối là Task 23); test gọi thẳng
        // OnPanelClicked/OnDragFinished nên accessor để rỗng (tránh CS0067).
        public event Action<int, int>? PanelClicked { add { } remove { } }
        public event Action<int, int>? DragFinished { add { } remove { } }
    }

    /// <summary>Overlay toggle fake: records calls and mirrors the enabled state. Fake công tắc overlay: ghi log và phản chiếu trạng thái.</summary>
    private sealed class FakeOverlay : IOverlayToggle
    {
        public bool IsEnabled { get; set; }
        public List<bool> Calls { get; } = new();
        public void SetEnabled(bool on) { Calls.Add(on); IsEnabled = on; }
    }

    /// <summary>
    /// Pipeline fake: records Start/Stop/ListSources calls, carries an optional start fault, raises
    /// nothing automatically. Fake pipeline: ghi log các lời gọi Start/Stop/ListSources, mang lỗi
    /// start tùy chọn, không tự phát event.
    /// </summary>
    private sealed class FakePipeline : IPipelineControl
    {
        public bool IsRunning { get; set; }
        public SourceSelection? ActiveSource { get; set; }
        public List<SourceSelection> Started { get; } = new();
        public int StopCalls { get; private set; }
        public int ListSourcesCalls { get; private set; }
        public (IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows) SourcesResult { get; set; } =
            (Array.Empty<MonitorInfo>(), Array.Empty<WindowInfo>());
        public Exception? StartException { get; set; }

        public Task StartAsync(SourceSelection source, CancellationToken ct)
        {
            Started.Add(source);
            if (StartException is not null) return Task.FromException(StartException);
            IsRunning = true;
            ActiveSource = source;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            StopCalls++;
            IsRunning = false;
            ActiveSource = null;
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<MonitorInfo> Monitors, IReadOnlyList<WindowInfo> Windows)> ListSourcesAsync(
            CancellationToken ct)
        {
            ListSourcesCalls++;
            return Task.FromResult(SourcesResult);
        }

        // Accessors rỗng: fake không bao giờ tự phát event — test raise thủ công qua pipeline thật
        // nếu cần; tránh CS0067 mà vẫn đúng hợp đồng IPipelineControl.
        // Empty accessors: the fake never raises on its own (tests drive state directly); avoids
        // CS0067 while keeping the IPipelineControl contract.
        public event Action? StateChanged { add { } remove { } }
        public event Action<bool, string?>? PipelineStateChanged { add { } remove { } }
    }

    /// <summary>Tray fake catching balloons — same pattern as the Plan-1 AppNotifier tests. Fake tray bắt balloon — cùng pattern với test AppNotifier của Plan 1.</summary>
    private sealed class FakeTray : ITrayHost
    {
        public List<(string Title, string Text, BalloonIcon Icon)> Balloons { get; } = new();
        public bool IsCreated => true;
        public void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider) { }
        public void Remove() { }
        public void ShowBalloon(string title, string text, BalloonIcon icon) => Balloons.Add((title, text, icon));
        // Accessor rỗng: fake không bao giờ bắn event — tránh CS0067 mà vẫn đúng hợp đồng ITrayHost.
        public event Action<TrayCommandId>? CommandSelected { add { } remove { } }
        public void Dispose() { }
    }

    /// <summary>
    /// Real settings at a temp path, real RunStats over real PreviewState/OverlayState with an
    /// injected clock, real AppNotifier behind a FakeTray (window hidden → balloon path), fake
    /// host/pipeline/overlay. The service's redraw clock is virtual so tests control the 66 ms
    /// leading-edge window deterministically.
    /// Settings thật tại đường dẫn tạm, RunStats thật trên PreviewState/OverlayState thật với đồng hồ
    /// inject, AppNotifier thật sau FakeTray (cửa sổ ẩn → nhánh balloon), host/pipeline/overlay fake.
    /// Đồng hồ redraw của service là ảo để test kiểm soát cửa sổ leading-edge 66 ms một cách quyết định.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _dir;
        private long _drawMs;     // virtual clock for the service's leading-edge throttle
        private long _statsTick;  // injected RunStats clock (frozen until advanced)

        public FakeMenuHost Host { get; } = new();
        public AppSettingsService Settings { get; }
        public FakePipeline Pipeline { get; } = new();
        public FakeOverlay OverlayToggle { get; } = new();
        public PreviewState Preview { get; } = new();
        public OverlayState Overlay { get; } = new();
        public RunStats Stats { get; }
        public ToastService Toast { get; } = new();
        public FakeTray Tray { get; } = new();
        public AppNotifier Notifier { get; }
        public I18nService L { get; } = new();
        public FloatingMenuService Service { get; }

        private Fixture(string dir, TimeSpan? redrawInterval)
        {
            _dir = dir;
            Settings = new AppSettingsService(Path.Combine(dir, "settings.json"));
            Stats = new RunStats(Preview, Overlay, Pipeline, now: () => _statsTick);
            Notifier = new AppNotifier(Toast, Tray, () => false, () => false, NullLogger<AppNotifier>.Instance);
            Service = new FloatingMenuService(Settings, OverlayToggle, Pipeline, Host, Notifier, Stats, L,
                NullLogger<FloatingMenuService>.Instance, redrawInterval);
            Service.DrawClock = () => DateTime.UnixEpoch.AddMilliseconds(_drawMs);
        }

        public static Fixture Create(TimeSpan? redrawInterval = null)
        {
            string dir = Path.Combine(Path.GetTempPath(), "retran-menu-tests", Guid.NewGuid().ToString("N"));
            return new Fixture(dir, redrawInterval);
        }

        /// <summary>Advances the virtual redraw clock past the default 66 ms window. Đẩy đồng hồ redraw ảo qua cửa sổ 66 ms mặc định.</summary>
        public void Advance(int ms = 100) => _drawMs += ms;

        /// <summary>Injected RunStats clock in milliseconds — advance before TickForTest to elapse the fps window. Đồng hồ inject của RunStats (ms) — đẩy lên trước TickForTest để trôi cửa sổ fps.</summary>
        public long StatsTick { get => _statsTick; set => _statsTick = value; }

        /// <summary>Persists settings through the real service (fires Changed like production). Lưu cài đặt qua service thật (phát Changed như production).</summary>
        public void Save(AppSettings s) => Settings.Save(s);

        public string SettingsPath => Path.Combine(_dir, "settings.json");

        public void Dispose()
        {
            Service.Dispose();
            Stats.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup is best effort */ }
        }
    }

    [Fact]
    public async Task Start_MonitorSource_StartsPipelineWithPersistedTriple()
    {
        using var fx = Fixture.Create();
        fx.Save(new AppSettings("google", "auto", "vi", null, SourceUseWindow: false, SourceMonitor: 1));
        fx.Service.Start();

        await fx.Service.StartPipelineAsync();

        Assert.Equal(new SourceSelection(false, 1, null), fx.Pipeline.Started.Single());
    }

    [Fact]
    public async Task Start_WindowSourceWithoutHwnd_ErrorsAndDoesNotStartOrMutateSettings()
    {
        using var fx = Fixture.Create();
        fx.Save(new AppSettings("google", "auto", "vi", null, SourceUseWindow: true, SourceMonitor: 0, SourceHwnd: null));
        fx.Service.Start();
        string before = File.ReadAllText(fx.SettingsPath);
        fx.Advance();

        await fx.Service.StartPipelineAsync();

        Assert.Empty(fx.Pipeline.Started);                        // D10: StartAsync không được gọi
        Assert.Equal(before, File.ReadAllText(fx.SettingsPath));  // settings giữ nguyên
        MenuState state = fx.Service.LastDrawnState!;
        Assert.Equal("menu.error.noWindow", state.ErrorLine);     // key chưa có (Task 21) → raw key
        var balloon = fx.Tray.Balloons.Single();
        Assert.Equal("menu.error.noWindow", balloon.Text);
        Assert.Equal(BalloonIcon.Error, balloon.Icon);
    }

    [Fact]
    public async Task Start_RpcError_SetsErrorLineAndNotifies()
    {
        using var fx = Fixture.Create();
        fx.Pipeline.StartException = new RpcException(-32000, "ocr-model-missing: PP-OCRv6_small_det");
        fx.Service.Start();
        fx.Advance();

        await fx.Service.StartPipelineAsync();

        MenuState state = fx.Service.LastDrawnState!;
        Assert.NotNull(state.ErrorLine);
        Assert.Equal("menu.error.startFailed", state.ErrorLine);  // raw key đến khi Task 21 thêm chuỗi
        var balloon = fx.Tray.Balloons.Single();
        Assert.Equal(state.ErrorLine, balloon.Text);
        Assert.Equal(BalloonIcon.Error, balloon.Icon);
    }

    [Fact]
    public void ToggleOverlay_WhileRunning_TogglesService()
    {
        using var fx = Fixture.Create();
        fx.Pipeline.IsRunning = true;
        fx.Service.Start();

        fx.Service.ToggleOverlay();
        Assert.True(fx.OverlayToggle.IsEnabled);
        Assert.Equal([true], fx.OverlayToggle.Calls);

        fx.Service.ToggleOverlay();
        Assert.False(fx.OverlayToggle.IsEnabled);
        Assert.Equal([true, false], fx.OverlayToggle.Calls);
    }

    [Fact]
    public void ToggleOverlay_WhileStopped_IsNoOp()
    {
        using var fx = Fixture.Create();
        fx.Pipeline.IsRunning = false;
        fx.Service.Start();

        fx.Service.ToggleOverlay();

        Assert.Empty(fx.OverlayToggle.Calls);  // spec §7.1: control disabled khi pipeline chưa chạy
        Assert.False(fx.OverlayToggle.IsEnabled);
    }

    [Fact]
    public void CycleDst_WrapsAndPersistsImmediately()
    {
        using var fx = Fixture.Create();
        fx.Save(new AppSettings("google", "auto", "es", null));  // mã cuối của DstCodes
        fx.Service.Start();

        fx.Service.CycleDst(true);   // es → vi (vòng về đầu)
        Assert.Equal("vi", fx.Settings.Load().Dst);

        fx.Service.CycleDst(false);  // vi → es (vòng về cuối)
        Assert.Equal("es", fx.Settings.Load().Dst);
    }

    [Fact]
    public void StepFps_ClampsAtBounds()
    {
        using var fx = Fixture.Create();
        fx.Save(new AppSettings("google", "auto", "vi", null, OcrFps: 60, CaptureFps: 1));
        fx.Service.Start();

        fx.Service.StepOcrFps(+1);     // 60 + 1 → kẹp về 60
        Assert.Equal(60, fx.Settings.Load().OcrFps);

        fx.Service.StepCaptureFps(-1); // 1 − 1 → kẹp về 1
        Assert.Equal(1, fx.Settings.Load().CaptureFps);

        fx.Service.StepOcrFps(-5);     // 60 − 5 = 55 (bước thường, không dính biên)
        Assert.Equal(55, fx.Settings.Load().OcrFps);
    }

    [Fact]
    public async Task OpenSourceList_CallsListSourcesOnce_AndBuildsItems()
    {
        using var fx = Fixture.Create();
        IReadOnlyList<MonitorInfo> monitors = [new MonitorInfo(0, "M1"), new MonitorInfo(1, "M2")];
        IReadOnlyList<WindowInfo> windows = [new WindowInfo("0x1", "Game", "game.exe")];
        fx.Pipeline.SourcesResult = (monitors, windows);
        fx.Service.Start();
        fx.Advance();

        await fx.Service.OpenSourceListAsync();

        Assert.Equal(1, fx.Pipeline.ListSourcesCalls);  // spec D11: chỉ gọi khi mở list
        MenuState state = fx.Service.LastDrawnState!;
        Assert.True(state.SourceListOpen);
        Assert.Equal(FloatingMenuLayout.BuildSources(monitors, windows), state.Sources);
    }

    [Fact]
    public void SelectSource_Window_PersistsTriple()
    {
        using var fx = Fixture.Create();
        fx.Pipeline.IsRunning = true;  // pipeline đang chạy: chọn nguồn không được Stop nó
        fx.Service.Start();

        fx.Service.SelectSource(new SourceItem(false, -1, "game.exe — Game", "0xABC"));

        AppSettings s = fx.Settings.Load();
        Assert.True(s.SourceUseWindow);
        Assert.Equal("0xABC", s.SourceHwnd);
        Assert.Equal(0, fx.Pipeline.StopCalls);  // không Stop hộ pipeline đang chạy

        fx.Service.SelectSource(new SourceItem(true, 1, "Màn hình 2", null));
        s = fx.Settings.Load();
        Assert.False(s.SourceUseWindow);
        Assert.Equal(1, s.SourceMonitor);
    }

    [Fact]
    public void CloseMenu_HidesAndPersistsOff()
    {
        using var fx = Fixture.Create();
        fx.Service.Start();
        Assert.True(fx.Service.IsVisible);

        fx.Service.CloseMenu();

        Assert.False(fx.Service.IsVisible);
        Assert.Equal(1, fx.Host.HideCalls);
        Assert.False(fx.Settings.Load().FloatingMenuOn);
    }

    [Fact]
    public void RapidStateChanges_DrawnAtMostEveryInterval()
    {
        using var fx = Fixture.Create(redrawInterval: TimeSpan.FromHours(1));  // cửa sổ throttle khổng lồ
        fx.Service.Start();  // vẽ ban đầu (bypass throttle — window vừa hiện không được để trống)

        fx.Service.CycleDst(true);
        fx.Service.CycleSrc(true);
        fx.Service.StepOcrFps(+1);
        fx.Service.StepCaptureFps(+1);
        fx.Service.ToggleMergeLines();  // 5 intent dồn dập, cùng mili-giây ảo

        Assert.True(fx.Host.DrawnBitmaps.Count <= 2,
            $"expected at most 2 draws in the throttle window, got {fx.Host.DrawnBitmaps.Count}");
    }

    [Fact]
    public void OnPanelClicked_Chip_ExpandsAndPersists()
    {
        using var fx = Fixture.Create();
        fx.Save(new AppSettings("google", "auto", "vi", null, FloatingMenuCollapsed: true));
        fx.Service.Start();  // window cỡ chip
        (int x0, int y0, int w0, int h0) = fx.Host.ShowCalls.Single();
        Assert.Equal(FloatingMenuLayout.ChipSize + 2 * FloatingMenuLayout.ShadowMargin, w0);
        fx.Advance();

        fx.Service.OnPanelClicked(20, 20);  // giữa chip (toạ độ PANEL — host đã trừ shadow)

        Assert.False(fx.Settings.Load().FloatingMenuCollapsed);
        MenuState state = fx.Service.LastDrawnState!;
        Assert.False(state.Collapsed);
        var last = fx.Host.ShowCalls[^1];
        Assert.True(last.W > w0, "expanding must resize the window to the full panel");
    }

    [Fact]
    public void StatsFromRunStats_LandInDrawnMenuState()
    {
        using var fx = Fixture.Create();
        fx.Service.Start();  // vẽ ban đầu @ t=0 (mọi số thống kê bằng 0)
        fx.Advance();        // mở cửa sổ leading-edge 66 ms

        fx.Pipeline.IsRunning = true;
        fx.Overlay.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            [new OverlayBox(0, 0, 1, 1, "a", "b"), new OverlayBox(0, 0, 1, 1, "c", "")]));  // 2 box, 1 dòng dịch
        for (int i = 0; i < 5; i++) fx.Preview.Publish(new PreviewFrame(i + 1, 1920, 1080, ""));
        fx.StatsTick += 1000;   // cửa sổ fps 1 s trôi qua
        fx.Stats.TickForTest(); // fps = 5 khung / 1 s → stats.Changed (bị throttle cùng mili-giây ảo)
        fx.Advance();           // mở cửa sổ redraw cho intent kế tiếp

        fx.Service.ToggleStats();  // intent thành công → dựng MenuState mới kèm số RunStats trực tiếp

        MenuState state = fx.Service.LastDrawnState!;
        Assert.True(state.StatsOpen);
        Assert.Equal(5.0, state.Fps, 2);
        Assert.Equal(2, state.BoxCount);
        Assert.Equal(1, state.TranslatedLineCount);
    }
}
