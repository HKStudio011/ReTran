using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Services.Tray;
using ReTran_App.Services.Update;

public class TrayExitServiceTests
{
    private sealed class FakeStopper : IUpdateStopper
    {
        public List<string> Calls { get; } = new();
        public bool ThrowOnStop { get; set; }
        public Task StopServicesAsync(CancellationToken ct)
        {
            Calls.Add("stop");
            if (ThrowOnStop) throw new IOException("simulated");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShutdown : IAppShutdown
    {
        public List<string> Calls { get; } = new();
        public void Quit() => Calls.Add("quit");
    }

    [Fact]
    public async Task Exit_StopsServices_BeforeQuit()
    {
        var stopper = new FakeStopper();
        var shutdown = new FakeShutdown();
        await new TrayExitService(stopper, shutdown, NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal(new[] { "stop", "quit" }, stopper.Calls.Concat(shutdown.Calls).ToArray());
    }

    [Fact]
    public async Task Exit_StopFails_StillQuits()
    {
        var stopper = new FakeStopper { ThrowOnStop = true };
        var shutdown = new FakeShutdown();
        await new TrayExitService(stopper, shutdown, NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal("quit", shutdown.Calls.Single());
    }
}
