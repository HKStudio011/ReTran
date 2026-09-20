using ReTran.Core.Capture;
using Xunit;

namespace RetranCore.Tests;

public class WindowCaptureTests
{
    [Fact]
    public void IsCapturable_Filters_Invisible_And_Untitled()
    {
        Assert.False(WindowCaptureSource.IsCapturable("", isVisible: true));
        Assert.False(WindowCaptureSource.IsCapturable("Notepad", isVisible: false));
        Assert.True(WindowCaptureSource.IsCapturable("Notepad", isVisible: true));
    }

    [Fact]
    public void EnumerateWindows_Returns_TitledWindows()
    {
        IReadOnlyList<WindowInfo> wins;
        try { wins = WindowCaptureSource.EnumerateWindows(); }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        { return; } // headless: skip
        Assert.All(wins, w => Assert.False(string.IsNullOrWhiteSpace(w.Title)));
    }
}
