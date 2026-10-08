using ReTran.Core.Cli;
using ReTran.Core.Translate;
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
    public async Task Translate_Success_PrintsTranslation_Exit0()
    {
        var d = new CommandDispatcher();
        var fake = new FakeTranslator("Xin chào");
        TranslateCommands.Register(d, fake);
        var (stdout, _, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["translate", "hello", "--dst", "vi", "--src", "en", "--provider", "fake"]);

        Assert.Equal(0, exit);
        Assert.Equal(
            new[] { "Xin chào" },
            stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(("hello", "en", "vi"), fake.LastCall);
    }

    [Fact]
    public async Task Translate_Failure_Exit1()
    {
        var d = new CommandDispatcher();
        TranslateCommands.Register(d, new ThrowingTranslator());
        var (stdout, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["translate", "hello", "--dst", "vi", "--src", "en", "--provider", "fake"]);

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        Assert.Contains("translate failed:", stderr, StringComparison.Ordinal);
    }

    private sealed class FakeTranslator(string result) : ITranslator
    {
        public string Name => "fake";

        public (string Text, string Src, string Dst)? LastCall { get; private set; }

        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct)
        {
            LastCall = (text, src, dst);
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingTranslator : ITranslator
    {
        public string Name => "fake";

        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct)
            => throw new TimeoutException("upstream timed out");
    }
}
