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
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false)); // idempotent
        Assert.True(host.IsCreated);
        host.Remove();
        host.ShowBalloon("t", "m", BalloonIcon.Info); // no-op sau Remove, không throw
    }
}
