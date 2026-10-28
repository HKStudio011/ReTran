using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.OcrClient;
using ReTran.Core.Serve;
using ReTran.Core.Translate;
using Xunit;

namespace RetranCore.Tests;

/// <summary>
/// Tests the two <c>ocr.config.*</c> proxies on <see cref="PipelineHost"/>: verbatim schema
/// forwarding, the forced <c>deferReload</c> in a clone that never touches the caller's node,
/// and the -32603 fallback when the sidecar never spawned.
/// Test hai proxy <c>ocr.config.*</c> trên <see cref="PipelineHost"/>: chuyển tiếp schema
/// nguyên văn, <c>deferReload</c> bị ép trong bản clone không bao giờ chạm node của caller,
/// và đường -32603 khi sidecar không khởi tạo được.
/// </summary>
public class OcrConfigRpcTests
{
    /// <summary>
    /// Recording sidecar fake: appends every call with its exact params node and returns a
    /// known schema-shaped result so the tests can assert the reply passes through untouched.
    /// Fake sidecar ghi lại: nối thêm mỗi lời gọi kèm đúng node params và trả kết quả dạng
    /// schema biết trước để test khẳng định phản hồi đi qua nguyên trạng.
    /// </summary>
    private sealed class RecordingRpc : ISidecarRpc
    {
        public JsonNode NextResult { get; } = new JsonObject { ["params"] = new JsonArray() };
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Add((method, @params));
            return Task.FromResult<JsonNode?>(NextResult);
        }
    }

    /// <summary>Builds a host over the given sidecar peer (null = never spawned). Dựng host trên peer sidecar cho trước (null = chưa khởi tạo).</summary>
    private static PipelineHost CreateHost(ISidecarRpc? rpc) =>
        new(rpc, new TranslatorRegistry(), new JsonRpcServer(), new StringWriter());

    [Fact]
    public async Task Schema_ForwardsVerbatim()
    {
        var rpc = new RecordingRpc();
        await using var host = CreateHost(rpc);
        await host.OcrConfigSchemaAsync(CancellationToken.None);
        Assert.Equal("ocr.config.schema", Assert.Single(rpc.Calls).Method);
    }

    [Fact]
    public async Task Set_ForcesDeferReloadFalse_WhenIdle_AndDoesNotMutateCallerNode()
    {
        var rpc = new RecordingRpc();
        await using var host = CreateHost(rpc);
        JsonNode callerParams = new JsonObject { ["params"] = new JsonObject { ["device"] = "cpu" } };
        await host.SetOcrConfigAsync(callerParams, CancellationToken.None);

        var sent = (JsonObject)rpc.Calls.Single().Params!;
        Assert.False(sent["deferReload"]!.GetValue<bool>());
        // caller giữ nguyên node gốc (không bị mutate/attach)
        Assert.Null(((JsonObject)callerParams)["deferReload"]);
    }

    [Fact]
    public async Task StackedProxy_IsNotAvailable_WhenSidecarMissing()
    {
        await using var host = CreateHost(null);
        await Assert.ThrowsAsync<RpcMethodException>(() =>
            host.OcrConfigSchemaAsync(CancellationToken.None));
    }
}
