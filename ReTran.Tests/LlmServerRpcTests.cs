using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlmServerRpcTests
{
    private const string ModelName = "Hy-MT2-1.8B-UD-Q4_K_XL";

    [Fact]
    public async Task StartRpc_ReturnsBeforeHealth_AndStatusIsReadable()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"retran-llm-server-rpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var host = new BlockingHost();
        var slot = new LlamaServerSlot((_, _) => host);
        var transport = new WireTransport();
        Task? serve = null;
        try
        {
            var models = new LlmModelManager(
                Path.Combine(dir, "models"), Path.Combine(dir, "models.json"));
            string modelPath = models.ModelPath(ModelName);
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            File.WriteAllBytes(modelPath, new byte[16]);
            string runtimeDir = Path.Combine(dir, "runtime", "cpu", "b190");
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllBytes(Path.Combine(runtimeDir, "llama-server.exe"), [0]);
            File.WriteAllText(Path.Combine(dir, "runtime", "runtime.json"),
                """{"tag":"b190","flavor":"cpu"}""");
            var runtime = new LlamaRuntimeManager(
                Path.Combine(dir, "runtime"), new HttpClient(new NeverHttpHandler()));
            var server = new JsonRpcServer();
            LlmServerRpc.Register(server, models, runtime, slot);
            serve = server.RunAsync(transport, TestContext.Current.CancellationToken);

            await transport.InjectAsync(JsonRpc.SerializeRequest(new JsonRpcRequest(
                "start", "llm.server.start", new JsonObject { ["name"] = ModelName })));
            JsonNode accepted = JsonRpc.ParseLine(
                await transport.WaitForResponseAsync("start"))!;
            Assert.Null(accepted["error"]);
            Assert.True(accepted["result"]!["accepted"]!.GetValue<bool>());
            long attempt = accepted["result"]!["attempt"]!.GetValue<long>();
            Assert.True(attempt > 0);
            Assert.Equal(ModelName, accepted["result"]!["model"]!.GetValue<string>());
            Assert.False(host.EnsureTask.IsCompleted); // the accepted response did not wait for health

            await transport.InjectAsync(JsonRpc.SerializeRequest(new JsonRpcRequest(
                "status", "llm.server.status", null)));
            JsonNode status = JsonRpc.ParseLine(
                await transport.WaitForResponseAsync("status"))!;
            Assert.Null(status["error"]);
            JsonNode result = status["result"]!;
            Assert.Equal("loading", result["state"]!.GetValue<string>());
            Assert.Equal(ModelName, result["model"]!.GetValue<string>());
            Assert.Equal(attempt, result["attempt"]!.GetValue<long>());
            Assert.Equal("loading", result["phase"]!.GetValue<string>());
            Assert.Equal(4 * 1024 * 1024, result["bytesLoaded"]!.GetValue<long>());
            Assert.Equal(8 * 1024 * 1024, result["bytesTotal"]!.GetValue<long>());
            Assert.Equal(1024, result["bytesPerSec"]!.GetValue<double>());
            Assert.True(result["progressAvailable"]!.GetValue<bool>());
            Assert.Null(result["error"]);
        }
        finally
        {
            host.ReleaseHealth();
            slot.Stop();
            transport.CompleteInput();
            if (serve is not null) await serve.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class BlockingHost : ILlamaServerHost
    {
        private readonly TaskCompletionSource _health = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _ensureTask;

        public string BaseUrl => "http://127.0.0.1:12345";
        public LlmLoadProgressSnapshot LoadProgress { get; } = new(
            "loading", 4 * 1024 * 1024, 8 * 1024 * 1024, 1024, true);
        public Task EnsureTask => _ensureTask ?? Task.CompletedTask;

        public Task EnsureStartedAsync(CancellationToken ct)
        {
            _ensureTask = _health.Task.WaitAsync(ct);
            return _ensureTask;
        }

        public void ReleaseHealth() => _health.TrySetResult();
        public void Dispose() => _health.TrySetCanceled();
    }

    private sealed class NeverHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The RPC status path must not access the network.");
    }

    private sealed class WireTransport : IJsonRpcTransport
    {
        private readonly object _gate = new();
        private readonly Queue<string> _input = new();
        private readonly List<string> _output = [];
        private SemaphoreSlim _inputSignal = new(0);
        private TaskCompletionSource _outputChanged = NewSignal();
        private bool _closed;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_input.Count > 0) return _input.Dequeue();
                    if (_closed) return null;
                    wait = _inputSignal.WaitAsync(ct);
                }
                await wait;
            }
        }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                _output.Add(line);
                changed = _outputChanged;
                _outputChanged = NewSignal();
            }
            changed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task InjectAsync(string line)
        {
            lock (_gate) _input.Enqueue(line);
            _inputSignal.Release();
            return Task.CompletedTask;
        }

        public async Task<string> WaitForResponseAsync(string id)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    foreach (string line in _output)
                    {
                        JsonNode? response = JsonRpc.ParseLine(line);
                        if (response?["id"]?.GetValueKind() == JsonValueKind.String
                            && response["id"]!.GetValue<string>() == id)
                        {
                            return line;
                        }
                    }
                    changed = _outputChanged.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                if (DateTime.UtcNow >= deadline) throw new TimeoutException($"No response for {id}.");
            }
        }

        public void CompleteInput()
        {
            lock (_gate) _closed = true;
            _inputSignal.Release();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
