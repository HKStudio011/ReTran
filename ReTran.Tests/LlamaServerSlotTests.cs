using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlamaServerSlotTests
{
    private sealed class FakeHost : ILlamaServerHost
    {
        private readonly TaskCompletionSource _ensureCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _startGate;

        public int DisposeCount { get; private set; }
        public Exception? StartError { get; set; }
        public string BaseUrl => "http://127.0.0.1:12345";
        public LlmLoadProgressSnapshot LoadProgress { get; } = new("loading", null, null, null, false);
        public Task EnsureCalled => _ensureCalled.Task;
        public Task? EnsureTask { get; private set; }

        public void BlockHealth() => _startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseHealth() => _startGate?.TrySetResult();

        public Task EnsureStartedAsync(CancellationToken ct)
        {
            _ensureCalled.TrySetResult();
            EnsureTask = StartError is not null
                ? Task.FromException(StartError)
                : _startGate is null ? Task.CompletedTask : _startGate.Task.WaitAsync(ct);
            return EnsureTask;
        }

        public void Dispose()
        {
            DisposeCount++;
            _startGate?.TrySetCanceled();
        }
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

    [Fact]
    public async Task Status_StartLifecycle_LoadingReadyThenStopped()
    {
        var host = new FakeHost();
        var slot = new LlamaServerSlot((_, _) => host);
        Assert.Equal("stopped", slot.Status.State);

        _ = slot.GetOrCreate("model-a", "server.exe", "a.gguf");
        Assert.Equal("loading", slot.Status.State);
        Assert.Equal("model-a", slot.Status.Model);

        await slot.EnsureStartedAsync(host, TestContext.Current.CancellationToken);
        Assert.Equal("ready", slot.Status.State);
        Assert.Null(slot.Status.Error);

        slot.Stop();
        Assert.Equal("stopped", slot.Status.State);
        Assert.Null(slot.Status.Model);
    }

    [Fact]
    public async Task Status_HealthFailure_ReportsErrorAndRethrows()
    {
        var host = new FakeHost { StartError = new InvalidOperationException("health timeout") };
        var slot = new LlamaServerSlot((_, _) => host);
        _ = slot.GetOrCreate("model-a", "server.exe", "a.gguf");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            slot.EnsureStartedAsync(host, TestContext.Current.CancellationToken));

        Assert.Equal("health timeout", error.Message);
        Assert.Equal("error", slot.Status.State);
        Assert.Equal("model-a", slot.Status.Model);
        Assert.Equal("health timeout", slot.Status.Error);
    }

    [Fact]
    public async Task Start_SameModelLoadingOrReady_IsIdempotent()
    {
        var hosts = new List<FakeHost>();
        var slot = new LlamaServerSlot((_, _) =>
        {
            var host = new FakeHost();
            host.BlockHealth();
            hosts.Add(host);
            return host;
        });

        long first = slot.Start("model-a", "server.exe", "a.gguf");
        long loading = slot.Start("model-a", "server.exe", "a.gguf");
        Assert.Equal(first, loading);
        Assert.Single(hosts);

        hosts[0].ReleaseHealth();
        await (hosts[0].EnsureTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        long ready = slot.Start("model-a", "server.exe", "a.gguf");

        Assert.Equal(first, ready);
        Assert.Single(hosts);
        slot.Stop();
    }

    [Fact]
    public async Task Start_ModelSwitch_OldCancellationCannotOverwriteNewAttempt()
    {
        var first = new FakeHost();
        first.BlockHealth();
        var second = new FakeHost();
        second.BlockHealth();
        var hosts = new Queue<FakeHost>([first, second]);
        var slot = new LlamaServerSlot((_, _) => hosts.Dequeue());

        long firstAttempt = slot.Start("model-a", "server.exe", "a.gguf");
        long secondAttempt = slot.Start("model-b", "server.exe", "b.gguf");
        await second.EnsureCalled.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.True(secondAttempt > firstAttempt);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal("model-b", slot.Status.Model);
        Assert.Equal(secondAttempt, slot.Status.Attempt);
        Assert.NotEqual("error", slot.Status.State);

        second.ReleaseHealth();
        await (second.EnsureTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        for (int i = 0; i < 20 && slot.Status.State != "ready"; i++) await Task.Yield();

        Assert.Equal("ready", slot.Status.State);
        Assert.Equal("model-b", slot.Status.Model);
        Assert.Equal(secondAttempt, slot.Status.Attempt);
        slot.Stop();
    }
}
