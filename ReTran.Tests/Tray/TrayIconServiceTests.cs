using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Services;
using ReTran_App.Services.Tray;

public class TrayIconServiceTests
{
    private sealed class FakeTrayHost : ITrayHost
    {
        public int CreateCount;
        public int RemoveCount;
        public List<string> Balloons { get; } = new();
        public bool IsCreated => true;
        public void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider) => CreateCount++;
        public void Remove() => RemoveCount++;
        public void ShowBalloon(string title, string text, BalloonIcon icon) => Balloons.Add(text);
        public event Action<TrayCommandId>? CommandSelected;
        public void Raise(TrayCommandId id) => CommandSelected?.Invoke(id);
        public void Dispose() { }
    }
    private sealed class FakeActions : ITrayActions
    {
        public bool OverlayOn { get; set; }
        public bool Running { get; set; } = true;
        public bool ThrowOnOverlay { get; set; }
        public List<string> Calls { get; } = new();
        public bool OverlayIsOn => OverlayOn;
        public bool PipelineRunning => Running;
        public void SetOverlay(bool on)
        {
            if (ThrowOnOverlay) throw new InvalidOperationException("simulated");
            OverlayOn = on;
            Calls.Add("overlay:" + on);
        }
        public void OpenAppWindow() => Calls.Add("open");
        public Task ExitApp() { Calls.Add("exit"); return Task.CompletedTask; }
    }

    private static (TrayIconService Svc, FakeTrayHost Host, FakeActions Actions, I18nService L) Create(bool overlayOn, bool running)
    {
        var host = new FakeTrayHost();
        var actions = new FakeActions { OverlayOn = overlayOn, Running = running };
        var l = new I18nService();
        l.Set("vi");
        return (new TrayIconService(host, actions, l, new AppNotifier(new ToastService(), host, () => false, () => false, NullLogger<AppNotifier>.Instance), NullLogger<TrayIconService>.Instance), host, actions, l);
    }

    [Fact]
    public void BuildMenu_ReflectsOverlayState()
    {
        var (svc, _, _, _) = Create(overlayOn: true, running: true);
        var menu = svc.BuildMenu();
        // IReadOnlyList<TrayCommand> không index được bằng enum (ruling Task 2) → Single theo Id.
        Assert.True(menu.Single(c => c.Id == TrayCommandId.ToggleOverlay).IsChecked);
    }

    [Fact]
    public void Selected_ToggleOverlay_FlipsAction()
    {
        var (svc, host, actions, _) = Create(overlayOn: false, running: true);
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay);
        Assert.Equal("overlay:True", actions.Calls.Single());
    }

    [Fact]
    public void Selected_ToggleOverlay_OnWhilePipelineStopped_StillSets_AndNotifies()
    {
        // Spec §7.2: vẫn cho toggle (áp dụng ở lần Start kế tiếp) + nói rõ, không ném.
        var (svc, host, actions, l) = Create(overlayOn: false, running: false);
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay);
        Assert.Equal("overlay:True", actions.Calls.Single());
        Assert.Equal(l["tray.overlay.appliesAtStart"], host.Balloons.Last());
    }

    [Fact]
    public void Selected_OpenApp_And_Exit()
    {
        var (svc, host, actions, _) = Create(overlayOn: false, running: true);
        svc.Start();
        host.Raise(TrayCommandId.OpenApp);
        host.Raise(TrayCommandId.Exit);
        Assert.Equal(new[] { "open", "exit" }, actions.Calls.ToArray());
    }

    [Fact]
    public void Selected_ActionThrows_BalloonReportsError()
    {
        // Spec §Báo lỗi (D12): MỌI lỗi khởi phát từ tray phải tới mắt người dùng —
        // bubble khi app ẩn/không focus (FakeTray + windowVisible=false ở helper Create).
        var (svc, host, actions, l) = Create(overlayOn: false, running: true);
        actions.ThrowOnOverlay = true;
        svc.Start();
        host.Raise(TrayCommandId.ToggleOverlay); // không được ném ra ngoài handler
        Assert.Equal(l["tray.error.command"], host.Balloons.Last());
    }

    [Fact]
    public void Start_Idempotent_StopRemoves_RestartRecreates()
    {
        var (svc, host, _, _) = Create(false, true);
        svc.Start();
        svc.Start();
        Assert.Equal(1, host.CreateCount);
        svc.Stop();
        Assert.Equal(1, host.RemoveCount);
        svc.Start();
        Assert.Equal(2, host.CreateCount);
    }
}
