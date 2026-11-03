using ReTran_App.Services.Tray;

public class Win32TrayHostSmokeTests
{
    [Fact]
    public void CreateRemoveDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var icon = new TrayIconSource();
        using var host = new Win32TrayHost();
        Assert.False(host.IsCreated);
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false));
        // Lần Create ĐẦU phải đăng ký THẬT (message-only window + NIM_ADD) — không phải cờ gán vô điều kiện.
        // The FIRST Create must REALLY register (message-only window + NIM_ADD) — not an unconditionally set flag.
        Assert.True(host.IsCreated);
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false)); // idempotent
        Assert.True(host.IsCreated);
        host.Remove();
        host.ShowBalloon("t", "m", BalloonIcon.Info); // no-op sau Remove, không throw
    }
}
