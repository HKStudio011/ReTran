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
/// never spawned. Also pins the serve-wire contract for the setup wizard's whole verb set —
/// including <c>sidecar.ping</c>/<c>sidecar.device</c> — resolving without -32601.
/// Test bốn proxy <c>ocr.models.*</c> trên <see cref="PipelineHost"/>: chuyển tiếp
/// method/params nguyên văn sang RPC sidecar và đường -32603 khi sidecar không khởi tạo được.
/// Đồng thời ghim hợp đồng wire serve cho trọn bộ verb của wizard thiết lập — kể cả
/// <c>sidecar.ping</c>/<c>sidecar.device</c> — đều resolve, không -32601.
/// </summary>
public class OcrModelRpcTests
{
    /// <summary>
    /// Recording sidecar fake: appends every call with the params it received and returns a
    /// known result so the tests can assert the reply passes through untouched. Per-method
    /// replies override the default so the wire test can give each verb its real reply shape.
    /// Fake sidecar ghi lại: nối thêm mỗi lời gọi kèm params nó nhận được và trả kết quả
    /// biết trước để test khẳng định phản hồi đi qua nguyên trạng. Phản hồi theo method ghi
    /// đè kết quả mặc định để test wire cho mỗi verb đúng hình dạng phản hồi thật của nó.
    /// </summary>
    private sealed class RecordingRpc : ISidecarRpc
    {
        public JsonNode NextResult { get; } = new JsonObject { ["ok"] = true };

        /// <summary>Per-method replies, consulted before <see cref="NextResult"/>. Phản hồi theo method, tra trước <see cref="NextResult"/>.</summary>
        public Dictionary<string, JsonNode?> ResultsByMethod { get; } = new(StringComparer.Ordinal);

        public List<(string Method, JsonNode? Params)> Calls { get; } = new();

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Add((method, @params));
            return Task.FromResult(ResultsByMethod.TryGetValue(method, out JsonNode? result) ? result : NextResult);
        }
    }

    /// <summary>
    /// One-shot in-memory serve wire: queued request lines are read to EOF and every response
    /// line is captured in dispatch order (the server's read loop processes lines sequentially).
    /// Wire serve in-memory một lượt: các dòng request xếp sẵn được đọc tới EOF và mọi dòng
    /// phản hồi được giữ lại theo đúng thứ tự dispatch (vòng đọc của server xử lý tuần tự).
    /// </summary>
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

    /// <summary>Builds a host over the given sidecar peer (null = never spawned). Dựng host trên peer sidecar cho trước (null = chưa khởi tạo).</summary>
    private static PipelineHost CreateHost(ISidecarRpc? rpc) =>
        new(rpc, new TranslatorRegistry(), new JsonRpcServer(), new StringWriter());

    /// <summary>
    /// The four proxy invocations exercised by the tests, each with the params node the
    /// caller sends (built once so the tests can compare the forwarded copy against it) and
    /// the exact method name the sidecar must receive.
    /// Bốn lời gọi proxy mà test dùng, mỗi cái kèm node params bên gửi (dựng một lần để test
    /// so sánh bản copy được chuyển tiếp với nó) và đúng tên method sidecar phải nhận.
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
            // Verbatim content: the sidecar sees a detached deep copy of the caller's params —
            // forwarding the caller's own node crashes request serialization whenever that node
            // is still a child of a parsed request envelope (see ForwardToSidecarAsync).
            // Nguyên văn nội dung: sidecar thấy bản deep copy tách rời của node params — chuyển
            // tiếp đúng node của bên gửi sẽ làm hỏng serialize request khi node đó còn là con của
            // một envelope request đã parse (xem ForwardToSidecarAsync).
            Assert.NotSame(expectedParams, recordedParams);
            Assert.Null(recordedParams!.Parent);
            Assert.Equal(expectedParams.ToJsonString(), recordedParams.ToJsonString());
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

    /// <summary>
    /// Contract re-pin for the smoke-blocker (task 23c): over the real serve wire
    /// (<see cref="JsonRpcServer"/> parsing and dispatching request lines), every verb the
    /// setup wizard calls through the core must resolve — no -32601. Before
    /// <c>sidecar.ping</c>/<c>sidecar.device</c> were registered, serve mode answered -32601
    /// for the first probe (and the device gate), so the VerifyingSidecar step could never
    /// pass. The trailing unknown verb proves the -32601 path itself still works: a green
    /// test is not vacuous.
    /// Ghim hợp đồng cho smoke-blocker (task 23c): qua đúng wire serve
    /// (<see cref="JsonRpcServer"/> parse và dispatch các dòng request), mọi verb mà wizard
    /// thiết lập gọi qua core đều phải resolve — không -32601. Trước khi
    /// <c>sidecar.ping</c>/<c>sidecar.device</c> được đăng ký, serve mode trả -32601 cho phép
    /// thử đầu tiên (và cổng thiết bị), nên bước VerifyingSidecar không bao giờ qua được.
    /// Verb lạ ở cuối chứng minh đường -32601 vẫn hoạt động: test xanh là không vô nghĩa.
    /// </summary>
    [Fact]
    public async Task ServeWire_WizardVerbSet_ResolvesWithoutMethodNotFound()
    {
        var rpc = new RecordingRpc();
        rpc.ResultsByMethod["sidecar.ping"] = JsonValue.Create("pong");
        rpc.ResultsByMethod["ocr.models.list"] = new JsonObject { ["catalog"] = new JsonArray() };
        rpc.ResultsByMethod["sidecar.device"] = JsonNode.Parse(
            """{"device":"gpu:0","capability":[12,0]}""")!;
        rpc.ResultsByMethod["ocr.models.recommend"] = new JsonObject { ["det"] = "d1", ["rec"] = "r1" };
        rpc.ResultsByMethod["ocr.models.download"] = new JsonObject { ["started"] = true };

        var server = new JsonRpcServer();
        await using var host = new PipelineHost(rpc, new TranslatorRegistry(), server, new StringWriter());
        // The same registrations serve mode installs (Program.RunServeAsync) — the wire must
        // answer all five; a verb left unregistered (the actual bug: sidecar.ping/
        // sidecar.device) answers -32601 instead of reaching the sidecar.
        // Đúng các registration mà serve mode cài (Program.RunServeAsync) — wire phải trả lời
        // đủ năm verb; verb không được đăng ký (đúng bug thật: sidecar.ping/sidecar.device)
        // trả -32601 thay vì chạm sidecar.
        server.Register("sidecar.ping", p => host.SidecarPingAsync(p, CancellationToken.None));
        server.Register("ocr.models.list", p => host.ListOcrModelsAsync(p, CancellationToken.None));
        server.Register("sidecar.device", p => host.SidecarDeviceAsync(p, CancellationToken.None));
        server.Register("ocr.models.recommend", p => host.RecommendOcrModelsAsync(p, CancellationToken.None));
        server.Register("ocr.models.download", p => host.DownloadOcrModelAsync(p, CancellationToken.None));

        // The exact call sequence of SetupCoordinator: ping, catalog, device, recommend, download.
        // Đúng chuỗi lời gọi của SetupCoordinator: ping, catalog, device, recommend, download.
        var transport = new BatchTransport([
            """{"jsonrpc":"2.0","id":"1","method":"sidecar.ping"}""",
            """{"jsonrpc":"2.0","id":"2","method":"ocr.models.list"}""",
            """{"jsonrpc":"2.0","id":"3","method":"sidecar.device"}""",
            """{"jsonrpc":"2.0","id":"4","method":"ocr.models.recommend","params":{"src":"ko"}}""",
            """{"jsonrpc":"2.0","id":"5","method":"ocr.models.download","params":{"name":"PP-OCRv6_small_det"}}""",
            """{"jsonrpc":"2.0","id":"6","method":"not.a.wizard.verb"}""",
        ]);
        await server.RunAsync(transport, CancellationToken.None);

        var byId = transport.Out.Select(line => JsonNode.Parse(line)!).ToDictionary(n => n["id"]!.GetValue<string>());
        Assert.Equal(6, transport.Out.Count);

        // All five wizard verbs resolve: a sidecar reply comes back, no -32601 (no error at all).
        // Cả năm verb wizard resolve: phản hồi sidecar quay về, không -32601 (không lỗi nào cả).
        foreach (string id in new[] { "1", "2", "3", "4", "5" })
            Assert.Null(byId[id]["error"]);
        Assert.Equal("pong", byId["1"]["result"]!.GetValue<string>());
        Assert.NotNull(byId["2"]["result"]!["catalog"]);
        Assert.Equal(12, byId["3"]["result"]!["capability"]![0]!.GetValue<int>());
        Assert.Equal("d1", byId["4"]["result"]!["det"]!.GetValue<string>());
        Assert.True(byId["5"]["result"]!["started"]!.GetValue<bool>());

        // The sidecar saw exactly the five calls, in order — the unknown verb never reached it.
        // Sidecar chỉ thấy đúng năm lời gọi, theo thứ tự — verb lạ không bao giờ chạm tới nó.
        string[] methods = [.. rpc.Calls.Select(c => c.Method)];
        Assert.Equal(
            new[] { "sidecar.ping", "ocr.models.list", "sidecar.device", "ocr.models.recommend", "ocr.models.download" },
            methods);

        // A truly unknown verb still answers -32601: the not-found path is intact.
        // Verb lạ hoàn toàn vẫn trả -32601: đường not-found còn nguyên.
        Assert.Equal(RpcErrorCodes.MethodNotFound, byId["6"]["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Method not found: not.a.wizard.verb", byId["6"]["error"]!["message"]!.GetValue<string>());
    }
}
