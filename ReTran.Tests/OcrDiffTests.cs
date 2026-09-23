using ReTran.Core.Ocr;
using Xunit;

namespace RetranCore.Tests;

public class OcrDiffTests
{
    [Fact]
    public void Feed_ReturnsNewTexts_ThenNothingForSame()
    {
        var diff = new OcrDiff();
        Assert.Equal(["HELLO", "WORLD"], diff.Feed(["HELLO", "WORLD"]));
        Assert.Empty(diff.Feed(["HELLO", "WORLD"]));
    }

    [Fact]
    public void Feed_IsCaseInsensitive()
    {
        var diff = new OcrDiff();
        Assert.Equal(["Hello"], diff.Feed(["Hello"]));
        Assert.Empty(diff.Feed(["HELLO"]));
    }

    [Fact]
    public void Feed_SkipsBlankTexts()
    {
        var diff = new OcrDiff();
        Assert.Equal(["A"], diff.Feed(["", "  ", "A"]));
        Assert.Empty(diff.Feed(["A", "  "]));
    }

    [Fact]
    public void Feed_PreservesInputOrder_AndNewOnly()
    {
        var diff = new OcrDiff();
        diff.Feed(["OLD"]);
        Assert.Equal(["N1", "N2"], diff.Feed(["OLD", "N1", "N2"]));
    }
}
