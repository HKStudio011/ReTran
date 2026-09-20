using ReTran.Core.Config;
using Xunit;

namespace RetranCore.Tests;

public class CaptureConfigTests
{
    [Fact]
    public void Defaults_Contain_CaptureValues()
    {
        var cfg = CoreConfig.LoadFrom(Path.Combine(Path.GetTempPath(), "retran-m1-missing.json"));
        Assert.Equal(0, cfg.CaptureMonitor);
        Assert.Equal(10, cfg.CaptureFps);
    }

    [Fact]
    public void Parses_CaptureValues()
    {
        string p = Path.Combine(Path.GetTempPath(), $"retran-m1-{Guid.NewGuid():N}.json");
        File.WriteAllText(p, "{\"captureMonitor\": 1, \"captureFps\": 15}");
        try { var cfg = CoreConfig.LoadFrom(p); Assert.Equal(1, cfg.CaptureMonitor); Assert.Equal(15, cfg.CaptureFps); }
        finally { File.Delete(p); }
    }
}
