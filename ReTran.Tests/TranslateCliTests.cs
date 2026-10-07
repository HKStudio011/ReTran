using ReTran.Core.Cli;
using Xunit;

namespace RetranCore.Tests;

public class TranslateCliTests
{
    [Fact]
    public async Task MissingDst_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "hi"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingText_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "--dst", "vi"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownArg_Exit2()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["translate", "hi", "--dst", "vi", "--bogus"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Google_Real_PrintsOneLine_OrSkipsWhenOffline()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d);
        var (stdout, _, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["translate", "hello", "--dst", "vi", "--src", "en"]);
        if (exit == 1) return; // google fail/offline -> skip-êm, không fail vì môi trường
        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }
}
