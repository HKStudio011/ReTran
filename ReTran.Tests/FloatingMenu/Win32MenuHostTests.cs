using ReTran_App.Services.FloatingMenu;
using ReTran_App.Services.OverlayWindow;

public class Win32MenuHostTests
{
    // Review Focus #2: the box window must insert after the menu hwnd so it always sits below
    // ReTran's own menu (both are topmost); fall back to HWND_TOPMOST before the menu exists.
    [Fact]
    public void BoxInsertAfter_MenusHwndWhenPresent()
    {
        Assert.Equal(42L, Win32OverlayHost.BoxInsertAfter(42).ToInt64());
        // HWND_TOPMOST is (nint)-1.
        Assert.Equal(-1L, Win32OverlayHost.BoxInsertAfter(0).ToInt64());
    }

    // Review Focus #1: the menu window must be topmost and non-activating, but NOT click-through
    // — it has to receive clicks (WS_EX_TRANSPARENT would eat them).
    [Fact]
    public void MenuWindowStyles_NoActivateAndTopmost()
    {
        uint styles = Win32OverlayHost.MenuWindowExStyles();

        const uint WS_EX_TOPMOST = 0x0008_0000;
        const uint WS_EX_NOACTIVATE = 0x0800_0000;
        const uint WS_EX_TRANSPARENT = 0x0000_0020;

        Assert.True((styles & WS_EX_TOPMOST) != 0, "expected WS_EX_TOPMOST to be set");
        Assert.True((styles & WS_EX_NOACTIVATE) != 0, "expected WS_EX_NOACTIVATE to be set");
        Assert.True((styles & WS_EX_TRANSPARENT) == 0, "WS_EX_TRANSPARENT must NOT be set — the menu receives clicks");
    }

    // Real-Win32 smoke: create → show → draw → hide → dispose without throwing.
    [Fact]
    public void CreateShowDrawHideDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new Win32OverlayHost();
        host.CreateMenu();
        Assert.True(host.MenuCreated); // IMenuWindowHost.MenuCreated
        host.ShowMenu(100, 100, 344, 500);
        using var bmp = new System.Drawing.Bitmap(344, 500, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        host.DrawMenu(bmp);
        host.HideMenu();
        host.Dispose();
    }
}
