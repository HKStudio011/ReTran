using System.Text.Json.Nodes;
using ReTran.Core;
using ReTran.Core.JsonRpc;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class ServeOcrTests
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

    private sealed class FakeRpc : ISidecarRpc
    {
        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(new JsonObject
            {
                ["spots"] = new JsonArray(new JsonObject
                {
                    ["box"] = new JsonArray(new JsonArray(0, 0), new JsonArray(1, 0),
                        new JsonArray(1, 1), new JsonArray(0, 1)),
                    ["text"] = "HI",
                    ["score"] = 0.95,
                }),
                ["elapsed_ms"] = 3,
            });
    }

    private static async Task<JsonNode> CallOcrSpotOnce(string requestLine, OcrPipeline? pipeline)
    {
        var server = new JsonRpcServer();
        Program.RegisterOcrMethod(server, pipeline);
        var t = new FakeTransport([requestLine]);
        await server.RunAsync(t, CancellationToken.None);
        return JsonNode.Parse(t.Out.Dequeue())!;
    }

    [Fact]
    public async Task OcrSpot_MissingImage_ReturnsInvalidParams()
    {
        var resp = await CallOcrSpotOnce(
            """{"jsonrpc":"2.0","id":"1","method":"ocr.spot","params":{}}""",
            new OcrPipeline(new FakeRpc(), new OcrDiff()));
        Assert.Equal(RpcErrorCodes.InvalidParams, resp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task OcrSpot_NoSidecar_ReturnsInternalError()
    {
        var resp = await CallOcrSpotOnce(
            """{"jsonrpc":"2.0","id":"2","method":"ocr.spot","params":{"image":"AAA"}}""",
            pipeline: null);
        Assert.Equal(RpcErrorCodes.InternalError, resp["error"]!["code"]!.GetValue<int>());
        Assert.Contains("sidecar", resp["error"]!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OcrSpot_HappyPath_ReturnsSpotsAndNewTexts()
    {
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var resp = await CallOcrSpotOnce(
            """{"jsonrpc":"2.0","id":"3","method":"ocr.spot","params":{"image":"AAA"}}""", pipeline);
        Assert.Equal("HI", resp["result"]!["spots"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("HI", resp["result"]!["new_texts"]![0]!.GetValue<string>());

        var second = await CallOcrSpotOnce(
            """{"jsonrpc":"2.0","id":"4","method":"ocr.spot","params":{"image":"BBB"}}""", pipeline);
        Assert.Empty(second["result"]!["new_texts"]!.AsArray());
    }
}
