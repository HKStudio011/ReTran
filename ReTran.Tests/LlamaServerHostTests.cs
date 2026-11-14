using System.Net;
using System.Net.Sockets;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlamaServerHostTests
{
    [Fact]
    public void BuildArguments_ContainsModelPortAndFlags()
    {
        string args = LlamaServerHost.BuildArguments(@"C:\models\m.gguf", 18080, 4096, 99);

        Assert.Contains("-m \"C:\\models\\m.gguf\"", args);
        Assert.Contains("--host 127.0.0.1", args);
        Assert.Contains("--port 18080", args);
        Assert.Contains("-c 4096", args);
        Assert.Contains("-ngl 99", args);
        Assert.Contains("--jinja", args);
    }

    [Fact]
    public void PickFreePort_IsBindable()
    {
        int port = LlamaServerHost.PickFreePort();

        Assert.InRange(port, 1024, 65535);
        // The port must still be bindable immediately after being returned.
        // Cổng phải còn bind được ngay sau khi trả về.
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task WaitForHealth_TrueWhenProbeSucceeds()
    {
        int calls = 0;

        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ => { calls++; return Task.FromResult(calls >= 2); },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task WaitForHealth_TimesOut()
    {
        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task WaitForHealth_ProbeThrows_KeepsTrying()
    {
        int calls = 0;

        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ =>
            {
                calls++;
                if (calls < 3) throw new HttpRequestException("connection refused");
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(ok);
    }
}
