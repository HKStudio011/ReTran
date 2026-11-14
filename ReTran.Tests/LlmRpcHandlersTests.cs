using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlmRpcHandlersTests
{
    /// <summary>One-shot in-memory serve wire (same pattern as the OCR RPC tests). Wire serve in-memory một lượt (cùng kiểu test RPC của OCR).</summary>
    private sealed class BatchTransport(IEnumerable<string> lines) : IJsonRpcTransport
    {
        private readonly Queue<string> _in = new(lines);

        public Queue<string> Out { get; } = new();

        public Task<string?> ReadLineAsync(CancellationToken ct) =>
            Task.FromResult(_in.Count > 0 ? _in.Dequeue() : null);

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            Out.Enqueue(line);
            return Task.CompletedTask;
        }
    }

    private sealed class GhHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private static (JsonRpcServer server, LlmModelManager models) CreateServer(string dir)
    {
        var server = new JsonRpcServer();
        var models = new LlmModelManager(
            Path.Combine(dir, "models"), Path.Combine(dir, "models.json"),
            (spec, path, ct) => File.WriteAllBytesAsync(path, new byte[4], ct));
        var runtime = new LlamaRuntimeManager(
            Path.Combine(dir, "runtime"),
            new HttpClient(new GhHandler("[]")),
            (uri, path, ct) => File.WriteAllBytesAsync(path, new byte[4], ct));
        LlmRpcHandlers.Register(server, models, runtime);
        return (server, models);
    }

    private static async Task<JsonNode> InvokeRawAsync(JsonRpcServer server, string method, JsonNode? @params)
    {
        var transport = new BatchTransport(
        [
            JsonRpc.SerializeRequest(new JsonRpcRequest("1", method, @params)),
        ]);
        await server.RunAsync(transport, CancellationToken.None);
        return JsonRpc.ParseLine(transport.Out.Dequeue())!;
    }

    private static string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"retran-llmrpc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ModelsList_ReturnsTwelveCatalogEntries()
    {
        string dir = NewDir();
        try
        {
            (JsonRpcServer server, _) = CreateServer(dir);

            JsonNode response = await InvokeRawAsync(server, "llm.models.list", null);

            Assert.Null(response["error"]);
            JsonArray catalog = response["result"]!.AsArray();
            Assert.Equal(12, catalog.Count);
            Assert.False(catalog[0]!["installed"]!.GetValue<bool>());
            Assert.False(catalog[0]!["downloading"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsDownload_UnknownName_InvalidParams()
    {
        string dir = NewDir();
        try
        {
            (JsonRpcServer server, _) = CreateServer(dir);

            JsonNode response = await InvokeRawAsync(
                server, "llm.models.download", new JsonObject { ["name"] = "nope" });

            Assert.Equal(RpcErrorCodes.InvalidParams, response["error"]!["code"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ModelsSet_NotInstalled_InvalidParams()
    {
        string dir = NewDir();
        try
        {
            (JsonRpcServer server, _) = CreateServer(dir);

            JsonNode response = await InvokeRawAsync(
                server, "llm.models.set", new JsonObject { ["name"] = "Hy-MT2-1.8B-Q4_K_M" });

            Assert.Equal(RpcErrorCodes.InvalidParams, response["error"]!["code"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeDownload_BadFlavor_InvalidParams()
    {
        string dir = NewDir();
        try
        {
            (JsonRpcServer server, _) = CreateServer(dir);

            JsonNode response = await InvokeRawAsync(
                server, "llm.runtime.download", new JsonObject { ["flavor"] = "vulkan" });

            Assert.Equal(RpcErrorCodes.InvalidParams, response["error"]!["code"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeStatus_BeforeDownload_NotInstalled()
    {
        string dir = NewDir();
        try
        {
            (JsonRpcServer server, _) = CreateServer(dir);

            JsonNode response = await InvokeRawAsync(server, "llm.runtime.status", null);

            Assert.Null(response["error"]);
            Assert.False(response["result"]!["installed"]!.GetValue<bool>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
