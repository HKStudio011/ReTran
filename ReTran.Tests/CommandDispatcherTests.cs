using ReTran.Core.Cli;
using Xunit;

namespace RetranCore.Tests;

public class CommandDispatcherTests
{
    [Fact]
    public async Task KnownSubcommand_InvokesHandler()
    {
        var d = new CommandDispatcher();
        string? seen = null;
        d.Register("version", async args => { seen = string.Join(' ', args); return 0; });
        int code = await d.DispatchAsync(new[] { "version" });
        Assert.Equal(0, code);
        Assert.Equal("", seen);
    }

    [Fact]
    public async Task UnknownSubcommand_ReturnsNonZero()
    {
        var d = new CommandDispatcher();
        int code = await d.DispatchAsync(new[] { "frobnicate" });
        Assert.NotEqual(0, code);
    }
}
