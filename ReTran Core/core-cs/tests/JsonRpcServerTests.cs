using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using Xunit;

namespace RetranCore.Tests;

public class JsonRpcServerTests
{
    private sealed class FakeTransport : IJsonRpcTransport
    {
        private readonly Queue<string> _in;
        public Queue<string> Out { get; } = new();
        public FakeTransport(IEnumerable<string> lines) { _in = new(lines); }
        public Task<string?> ReadLineAsync(CancellationToken ct) =>
            Task.FromResult(_in.Count > 0 ? _in.Dequeue() : null);
        public Task WriteLineAsync(string line, CancellationToken ct) { Out.Enqueue(line); return Task.CompletedTask; }
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
}
