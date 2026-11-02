using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Services.Tray;
using ReTran_App.Services.Update;

public class TrayExitServiceTests
{
    // Hai fake dùng chung MỘT call list — thứ tự stop→quit (Review Focus #4) phải được khóa
    // bằng chứng ghi đè thật, không phải bằng Concat hai list riêng (luôn ra đúng thứ tự mong đợi).
    private sealed class FakeStopper : IUpdateStopper
    {
        private readonly List<string> _calls;
        public bool ThrowOnStop { get; set; }
        public FakeStopper(List<string> calls) => _calls = calls;
        public Task StopServicesAsync(CancellationToken ct)
        {
            _calls.Add("stop");
            if (ThrowOnStop) throw new IOException("simulated");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShutdown : IAppShutdown
    {
        private readonly List<string> _calls;
        public FakeShutdown(List<string> calls) => _calls = calls;
        public void Quit() => _calls.Add("quit");
    }

    [Fact]
    public async Task Exit_StopsServices_BeforeQuit()
    {
        var calls = new List<string>();
        await new TrayExitService(new FakeStopper(calls), new FakeShutdown(calls),
            NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal(new[] { "stop", "quit" }, calls.ToArray());
    }

    [Fact]
    public async Task Exit_StopFails_StillQuits()
    {
        var calls = new List<string>();
        await new TrayExitService(new FakeStopper(calls) { ThrowOnStop = true }, new FakeShutdown(calls),
            NullLogger<TrayExitService>.Instance).ExitAsync(CancellationToken.None);

        Assert.Equal(new[] { "stop", "quit" }, calls.ToArray());
    }
}
