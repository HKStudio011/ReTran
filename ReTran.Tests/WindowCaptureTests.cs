using System.Runtime.InteropServices;
using ReTran.Core.Capture;
using Xunit;

namespace RetranCore.Tests;

public class WindowCaptureTests
{
    [Fact]
    public void DwmThumbnailProperties_MatchNativeLayout()
    {
        // Native layout: flags(4) + rcDestination(16) + rcSource(16) + opacity(1) + pad(3)
        // + fVisible BOOL(4) + fSourceClientAreaOnly BOOL(4) = 48. A C# bool would shrink the
        // struct, and DwmUpdateThumbnailProperties would read past it.
        // Bố cục native: ... = 48. Dùng bool C# sẽ làm struct ngắn đi và lời gọi DWM đọc quá biên.
        Assert.Equal(48, Marshal.SizeOf<NativeMethods.DWM_THUMBNAIL_PROPERTIES>());
    }

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
