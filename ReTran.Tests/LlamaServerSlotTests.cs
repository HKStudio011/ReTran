using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlamaServerSlotTests
{
    private sealed class FakeHost : ILlamaServerHost
    {
        public int DisposeCount { get; private set; }
        public string BaseUrl => "http://127.0.0.1:12345";
        public Task EnsureStartedAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => DisposeCount++;
    }

    [Fact]
    public void Stop_CoreStopAndRepeatedStop_DisposesHostOnce()
    {
        var host = new FakeHost();
        var slot = new LlamaServerSlot((_, _) => host);
        Assert.Same(host, slot.GetOrCreate("model-a", "server.exe", "a.gguf"));

        slot.Stop(); // core.stop after a local session
        slot.Stop(); // shutdown path may repeat cleanup

        Assert.Equal(1, host.DisposeCount);
    }

    [Fact]
    public void Stop_LocalToApiTransition_ClearsHostForFutureLocalSession()
    {
        var hosts = new List<FakeHost>();
        var slot = new LlamaServerSlot((_, _) =>
        {
            var host = new FakeHost();
            hosts.Add(host);
            return host;
        });
        ILlamaServerHost local = slot.GetOrCreate("model-a", "server.exe", "a.gguf");

        slot.Stop(); // local → API must release llama.cpp/VRAM
        ILlamaServerHost nextLocal = slot.GetOrCreate("model-a", "server.exe", "a.gguf");

        Assert.Equal(1, hosts[0].DisposeCount);
        Assert.Equal(2, hosts.Count);
        Assert.NotSame(local, nextLocal);
    }

    [Fact]
    public void GetOrCreate_ModelSwitch_DisposesPreviousHost()
    {
        var hosts = new List<FakeHost>();
        var slot = new LlamaServerSlot((_, _) =>
        {
            var host = new FakeHost();
            hosts.Add(host);
            return host;
        });
        _ = slot.GetOrCreate("model-a", "server.exe", "a.gguf");

        _ = slot.GetOrCreate("model-b", "server.exe", "b.gguf");

        Assert.Equal(1, hosts[0].DisposeCount);
        Assert.Equal(0, hosts[1].DisposeCount);
    }
}
