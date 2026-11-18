using System.IO.Compression;
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
    public async Task RuntimeCheck_CpuFlavor_UsesCpuAssets()
    {
        string dir = NewDir();
        try
        {
            const string releases = """
                [
                  {
                    "tag_name": "b200",
                    "assets": [ { "name": "llama-b200-bin-win-cpu-x64.zip", "browser_download_url": "http://x/cpu200.zip" } ]
                  },
                  {
                    "tag_name": "b190",
                    "assets": [
                      { "name": "llama-b190-bin-win-cuda-x64.zip", "browser_download_url": "http://x/cuda190.zip" },
                      { "name": "llama-b190-bin-win-avx2-x64.zip", "browser_download_url": "http://x/avx2190.zip" }
                    ]
                  }
                ]
                """;
            var server = new JsonRpcServer();
            var models = new LlmModelManager(
                Path.Combine(dir, "models"), Path.Combine(dir, "models.json"));
            var runtime = new LlamaRuntimeManager(
                Path.Combine(dir, "runtime"), new HttpClient(new GhHandler(releases)));
            LlmRpcHandlers.Register(server, models, runtime);

            JsonNode response = await InvokeRawAsync(
                server, "llm.runtime.check", new JsonObject { ["flavor"] = "cpu" });

            Assert.Null(response["error"]);
            Assert.Equal("b190", response["result"]!["latest"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RuntimeDownload_AcceptReturnsAttempt_AndImmediateStatusIsDownloading()
    {
        string dir = NewDir();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            bool block = false;
            var runtime = new LlamaRuntimeManager(
                Path.Combine(dir, "runtime"), new HttpClient(new GhHandler("""
                    [ { "tag_name": "b190", "assets": [ { "name": "llama-b190-bin-win-avx2-x64.zip", "browser_download_url": "http://x/avx2190.zip" } ] } ]
                    """)),
                async (uri, target, ct) =>
                {
                    if (block) await gate.Task.WaitAsync(ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using FileStream fs = File.Create(target);
                    using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                    zip.CreateEntry("llama-server.exe");
                });
            await runtime.DownloadAsync("cpu", TestContext.Current.CancellationToken);
            block = true;
            var server = new JsonRpcServer();
            var models = new LlmModelManager(Path.Combine(dir, "models"), Path.Combine(dir, "models.json"));
            LlmRpcHandlers.Register(server, models, runtime);

            JsonNode accepted = await InvokeRawAsync(
                server, "llm.runtime.download", new JsonObject { ["flavor"] = "cpu" });
            long attempt = accepted["result"]!["attempt"]!.GetValue<long>();
            JsonNode status = await InvokeRawAsync(server, "llm.runtime.status", null);

            Assert.True(attempt > 0);
            Assert.Equal(attempt, status["result"]!["attempt"]!.GetValue<long>());
            Assert.True(status["result"]!["downloading"]!.GetValue<bool>());
            Assert.False(status["result"]!["installed"]!.GetValue<bool>());
            gate.SetResult();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!(await runtime.GetStatusAsync(TestContext.Current.CancellationToken)).Installed
                   && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
            }
            Assert.True((await runtime.GetStatusAsync(TestContext.Current.CancellationToken)).Installed);
        }
        finally
        {
            gate.TrySetResult();
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
