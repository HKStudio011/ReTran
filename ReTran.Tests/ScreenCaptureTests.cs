using ReTran.Core.Capture;
using Xunit;

namespace RetranCore.Tests;

public class ScreenCaptureTests
{
    [Fact]
    public void EnumerateMonitors_Returns_AtLeastOne()
    {
        IReadOnlyList<MonitorInfo> monitors;
        try { monitors = ScreenCaptureSource.EnumerateMonitors(); }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        { return; } // headless/CI không có DXGI: skip, không fail
        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].Width > 0 && monitors[0].Height > 0);
    }
}
