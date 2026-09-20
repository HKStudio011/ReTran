using ReTran.Core.Capture;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class CaptureCliTests
{
    [Fact]
    public void WriteBgra_Produces_ValidPngFile()
    {
        using var f = Frame.FromBgra(8, 8, Enumerable.Repeat((byte)128, 8 * 8 * 4).ToArray());
        string p = Path.Combine(Path.GetTempPath(), $"retran-test-{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.WriteBgra(p, f);
            byte[] magic = File.ReadAllBytes(p).Take(8).ToArray();
            byte[] pngMagic = [137, 80, 78, 71, 13, 10, 26, 10];
            Assert.Equal(pngMagic, magic);
        }
        finally { if (File.Exists(p)) File.Delete(p); }
    }

    [Fact]
    public void ParseHwnd_Accepts_HexAndDecimal()
    {
        Assert.Equal((nint)0x1234, CaptureCommands.ParseHwnd("0x1234"));
        Assert.Equal((nint)4660, CaptureCommands.ParseHwnd("4660"));
    }
}
