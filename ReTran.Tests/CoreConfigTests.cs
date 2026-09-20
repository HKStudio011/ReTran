using ReTran.Core.Config;
using Xunit;

namespace RetranCore.Tests;

public class CoreConfigTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var cfg = CoreConfig.LoadFrom(Path.Combine(Path.GetTempPath(), "retran-test-missing.json"));
        Assert.Equal(3, cfg.OcrFps);
    }

    [Fact]
    public void Load_Parses_OcrFps()
    {
        string p = Path.Combine(Path.GetTempPath(), "retran-test-cfg.json");
        File.WriteAllText(p, "{\"ocrFps\": 5}");
        var cfg = CoreConfig.LoadFrom(p);
        Assert.Equal(5, cfg.OcrFps);
        File.Delete(p);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileMalformed()
    {
        string p = Path.Combine(Path.GetTempPath(), "retran-test-malformed.json");
        File.WriteAllText(p, "{not json");
        try
        {
            var cfg = CoreConfig.LoadFrom(p);
            Assert.Equal(3, cfg.OcrFps);
        }
        finally
        {
            File.Delete(p);
        }
    }
}
