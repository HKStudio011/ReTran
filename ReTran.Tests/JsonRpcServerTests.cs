using System.Text.Json;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using Xunit;

namespace RetranCore.Tests;

public class JsonRpcServerTests
{
    private sealed class FakeTransport : IJsonRpcTransport
    {
        private readonly Queue<string> _in = new();
        private readonly SemaphoreSlim _inSignal = new(0);
        private readonly object _gate = new();
        private readonly bool _inputOpen;
        private bool _inputClosed;

        public Queue<string> Out { get; } = new();
        public List<string> WrittenLines { get; } = new();
        public string? LastRequest { get; private set; }

        public FakeTransport(IEnumerable<string> lines)
        {
            foreach (string line in lines) _in.Enqueue(line);
            _inputOpen = false;
        }

        public FakeTransport() => _inputOpen = true;

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_in.Count > 0) return _in.Dequeue();
                    if (!_inputOpen || _inputClosed) return null;
                }
                await _inSignal.WaitAsync(ct);
            }
        }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            lock (_gate)
            {
                Out.Enqueue(line);
                WrittenLines.Add(line);
                if (JsonRpc.ParseLine(line)?["method"] is not null) LastRequest = line;
            }
            return Task.CompletedTask;
        }

        public Task InjectLineAsync(string line)
        {
            lock (_gate) _in.Enqueue(line);
            _inSignal.Release();
            return Task.CompletedTask;
        }

        public void CompleteInput()
        {
            lock (_gate) _inputClosed = true;
            _inSignal.Release();
        }

        public async Task WaitForLinesAsync(int n)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (_gate) { if (Out.Count >= n) return; }
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {n} written lines.");
                await Task.Delay(10);
            }
        }

        public async Task<string> WaitForResponseAsync(string id)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                lock (_gate)
                {
                    foreach (string line in WrittenLines)
                    {
                        JsonNode? node = JsonRpc.ParseLine(line);
                        if (node is null || node["method"] is not null) continue;
                        JsonNode? respId = node["id"];
                        if (respId is not null && respId.GetValueKind() == JsonValueKind.String
                            && respId.GetValue<string>() == id)
                            return line;
                    }
                }
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"No response with id {id}.");
                await Task.Delay(10);
            }
        }
    }

    [Fact]
    public async Task Ping_ReturnsPong()
    {
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        var t = new FakeTransport(new[] { "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"core.ping\",\"params\":{}}" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal("pong", resp["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var server = new JsonRpcServer();
        var t = new FakeTransport(new[] { "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"core.nope\",\"params\":{}}" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal(RpcErrorCodes.MethodNotFound, resp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedLine_ReturnsParseError()
    {
        var server = new JsonRpcServer();
        var t = new FakeTransport(new[] { "this is not json" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal(RpcErrorCodes.ParseError, resp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task CallAsync_Completes_WhenMatchingResponseArrives()
    {
        var t = new FakeTransport();
        var server = new JsonRpcServer();
        _ = server.RunAsync(t, CancellationToken.None);
        Task<JsonNode?> call = server.CallAsync("translate.request_cache", new JsonObject(), CancellationToken.None);
        Assert.Equal("translate.request_cache", JsonNode.Parse(t.LastRequest!)!["method"]!.GetValue<string>());
        string id = JsonNode.Parse(t.LastRequest!)!["id"]!.GetValue<string>();
        await t.InjectLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":{{\"hits\":{{}}}}}}");
        Assert.NotNull((await call)!["hits"]);
    }

    [Fact]
    public async Task Response_WithoutMethod_RoutesToPending_NotParseError()
    {
        var t = new FakeTransport();
        var server = new JsonRpcServer();
        _ = server.RunAsync(t, CancellationToken.None);
        Task<JsonNode?> call = server.CallAsync("m", null, CancellationToken.None);
        string id = JsonNode.Parse(t.LastRequest!)!["id"]!.GetValue<string>();
        await t.InjectLineAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"result\":\"ok\"}}");
        Assert.Equal("ok", (await call)!.GetValue<string>());
        Assert.DoesNotContain(t.WrittenLines, line => line.Contains("Parse error")); // không được phát sinh parse-error
    }

    [Fact]
    public async Task ConcurrentNotify_DuringDispatch_WritesCleanLines()
    {
        var t = new FakeTransport();
        var server = new JsonRpcServer();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Register("slow", async _ => { await gate.Task; return JsonValue.Create("done"); });
        _ = server.RunAsync(t, CancellationToken.None);
        await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"a\",\"method\":\"slow\"}");
        Task notify1 = server.NotifyAsync("frame.translated", new JsonObject(), TestContext.Current.CancellationToken);
        Task notify2 = server.NotifyAsync("pipeline.state", new JsonObject(), TestContext.Current.CancellationToken);
        gate.SetResult();
        await Task.WhenAll(notify1, notify2);
        await t.WaitForLinesAsync(3);
        foreach (string line in t.WrittenLines)
            Assert.NotNull(JsonNode.Parse(line)); // mọi dòng đều JSON hợp lệ, không torn
    }

    [Fact]
    public async Task CallAsync_UnknownIdResponse_IsIgnored()
    {
        var t = new FakeTransport();
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        _ = server.RunAsync(t, CancellationToken.None);
        await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"999\",\"result\":\"stray\"}");
        await t.InjectLineAsync("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"core.ping\"}");
        var resp = JsonNode.Parse((await t.WaitForResponseAsync("1"))!)!;
        Assert.Equal("pong", resp["result"]!.GetValue<string>()); // response lạ bị bỏ qua, dispatch chạy tiếp FIFO
        Assert.Single(t.WrittenLines);                             // không có dòng nào phát sinh từ id "999"
    }
}
