using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.OcrClient;
using ReTran.Core.Serve;
using ReTran.Core.Translate;
using Xunit;

namespace RetranCore.Tests;

/// <summary>
/// Tests the four <c>ocr.models.*</c> proxies on <see cref="PipelineHost"/>: verbatim
/// method/params forwarding to the sidecar RPC and the -32603 fallback when the sidecar
/// never spawned.
/// Test bốn proxy <c>ocr.models.*</c> trên <see cref="PipelineHost"/>: chuyển tiếp
/// method/params nguyên văn sang RPC sidecar và đường -32603 khi sidecar không khởi tạo được.
/// </summary>
public class OcrModelRpcTests
{
    /// <summary>
    /// Recording sidecar fake: appends every call with its exact params node and returns a
    /// known result so the tests can assert the reply passes through untouched.
    /// Fake sidecar ghi lại: nối thêm mỗi lời gọi kèm đúng node params và trả kết quả
    /// biết trước để test khẳng định phản hồi đi qua nguyên trạng.
    /// </summary>
    private sealed class RecordingRpc : ISidecarRpc
    {
        public JsonNode NextResult { get; } = new JsonObject { ["ok"] = true };
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

    /// <summary>
    /// The four proxy invocations exercised by the tests, each with the params node the
    /// caller sends (built once so the tests can assert it is forwarded by reference) and
    /// the exact method name the sidecar must receive.
    /// Bốn lời gọi proxy mà test dùng, mỗi cái kèm node params bên gửi (dựng một lần để
    /// test khẳng định nó được chuyển tiếp nguyên bản) và đúng tên method sidecar phải nhận.
    /// </summary>
    private static (string Method, JsonNode Params, Func<PipelineHost, Task<JsonNode?>> Invoke)[] Proxies()
    {
        JsonNode listParams = new JsonObject { ["ignored"] = true };
        JsonNode downloadParams = new JsonObject { ["name"] = "PP-OCRv6_small_det" };
        JsonNode deleteParams = new JsonObject { ["name"] = "PP-OCRv6_small_rec" };
        JsonNode setParams = new JsonObject { ["det"] = "PP-OCRv6_small_det", ["rec"] = "PP-OCRv6_small_rec" };
        return
        [
            ("ocr.models.list", listParams,
                host => host.ListOcrModelsAsync(listParams, CancellationToken.None)),
            ("ocr.models.download", downloadParams,
                host => host.DownloadOcrModelAsync(downloadParams, CancellationToken.None)),
            ("ocr.models.delete", deleteParams,
                host => host.DeleteOcrModelAsync(deleteParams, CancellationToken.None)),
            ("ocr.models.set", setParams,
                host => host.SetOcrModelPairAsync(setParams, CancellationToken.None)),
        ];
    }

    [Fact]
    public async Task Proxies_ForwardMethodAndParamsVerbatim()
    {
        var rpc = new RecordingRpc();
        await using var host = CreateHost(rpc);

        foreach ((string method, JsonNode expectedParams, Func<PipelineHost, Task<JsonNode?>> invoke) in Proxies())
        {
            JsonNode? result = await invoke(host);

            (string recordedMethod, JsonNode? recordedParams) =
                Assert.Single(rpc.Calls.Where(c => c.Method == method).ToArray());
            Assert.Equal(method, recordedMethod);
            // Verbatim: the sidecar sees the very same params node the caller passed.
            // Nguyên văn: sidecar thấy đúng node params mà bên gửi truyền.
            Assert.Same(expectedParams, recordedParams);
            // And the sidecar's reply rides back untouched.
            // Phản hồi của sidecar quay về nguyên trạng.
            Assert.Same(rpc.NextResult, result);
        }
    }

    [Fact]
    public async Task Proxies_WithoutSidecar_ThrowInternalError()
    {
        await using var host = CreateHost(null);

        foreach ((string _, JsonNode _, Func<PipelineHost, Task<JsonNode?>> invoke) in Proxies())
        {
            var ex = await Assert.ThrowsAsync<RpcMethodException>(() => invoke(host));
            Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
            Assert.Equal("sidecar not available (python/venv missing or spawn failed).", ex.Message);
        }
    }
}
