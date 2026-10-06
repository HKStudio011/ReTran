using System.Runtime.InteropServices;
using ReTran.Core.Serve;

namespace RetranCore.Tests;

public class ContextKeyTests
{
    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [Fact] public void FromMonitor_FormatsScreenKey() => Assert.Equal("screen:2", ContextKey.FromMonitor(2));

    [Fact]
    public void FromHwnd_DesktopWindow_ReturnsCsrssExe()
    {
        // controller ruling T-F2: brief ghi "desktop thuộc explorer.exe" — fact-sai;
        // desktop window #32769 thuộc csrss.exe (verify empirical trên máy này).
        string key = ContextKey.FromHwnd(GetDesktopWindow());
        Assert.Equal("csrss.exe", key);
    }

    [Fact] public void FromHwnd_Zero_ReturnsUnknown() => Assert.Equal("unknown", ContextKey.FromHwnd(0));
}
