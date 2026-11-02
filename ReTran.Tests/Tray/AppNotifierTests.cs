using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Services;
using ReTran_App.Services.Tray;

public class AppNotifierTests
{
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

    [Theory]
    [InlineData(false, false, true)]  // app ẩn → bubble dù có focus hay không
    [InlineData(true, false, true)]   // app hiện nhưng đang chơi game (không focus) → bubble
    [InlineData(true, true, false)]   // app đang focus → toast trong app
    public void UseTrayBubble_Matrix(bool visible, bool focused, bool expected)
        => Assert.Equal(expected, NotifyRoute.UseTrayBubble(visible, focused));

    [Fact]
    public void Notify_FocusedWindow_UsesToast_NotBalloon()
    {
        var tray = new FakeTray();
        var toast = new ToastService();
        string? toasted = null;
        toast.ToastAdded += t => toasted = t.Message;
        var notifier = new AppNotifier(toast, tray, () => true, () => true, NullLogger<AppNotifier>.Instance);

        notifier.Notify("Lỗi gì đó", ToastSeverity.Error);

        Assert.Equal("Lỗi gì đó", toasted);
        Assert.Empty(tray.Balloons);
    }

    [Fact]
    public void Notify_HiddenWindow_UsesBalloon_NotToast()
    {
        var tray = new FakeTray();
        var toast = new ToastService();
        bool toasted = false;
        toast.ToastAdded += _ => toasted = true;
        var notifier = new AppNotifier(toast, tray, () => false, () => false, NullLogger<AppNotifier>.Instance);

        notifier.Notify("Lỗi nặng", ToastSeverity.Error);

        Assert.False(toasted);
        var (title, text, icon) = tray.Balloons.Single();
        // Title là chuỗi thương hiệu cố định, không i18n (ruling ledger 2026-11-02).
        Assert.Equal("ReTran", title);
        Assert.Equal("Lỗi nặng", text);
        Assert.Equal(BalloonIcon.Error, icon);
    }

    [Fact]
    public void Notify_ManyErrorsWithinCooldown_OneBalloon()
    {
        var tray = new FakeTray();
        var notifier = new AppNotifier(new ToastService(), tray, () => false, () => false,
            NullLogger<AppNotifier>.Instance, bubbleCooldown: TimeSpan.FromSeconds(30));

        notifier.Notify("e1");
        notifier.Notify("e2");
        notifier.Notify("e3");

        // Assert.Single thay vì Assert.Equal(1, count): xUnit2013 đòi assertion chuyên dụng.
        Assert.Single(tray.Balloons);
    }

    [Fact]
    public void LevelFor_MapsSeverity()
    {
        Assert.Equal(BalloonIcon.Info, AppNotifier.LevelFor(ToastSeverity.Info));
        Assert.Equal(BalloonIcon.Error, AppNotifier.LevelFor(ToastSeverity.Error));
    }
}
