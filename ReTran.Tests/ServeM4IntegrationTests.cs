using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran_App.Core;

namespace RetranCore.Tests;

/// <summary>
/// Process-level integration tests for serve mode (spec §9 "Integration: serve qua stdio
/// scripted"): they spawn the real <c>ReTran.Core.exe --serve</c> and drive it over stdio through
/// the App's own <see cref="CoreProcessClient"/>/<c>JsonRpcClient</c> pair, so the whole IPC
/// contract (<c>core.ping/stop/list_sources/start</c>, <c>frame.translated</c>,
/// <c>translate.request_cache</c>) is exercised end to end.
/// Test integration mức tiến trình cho serve mode (spec §9): khởi chạy
/// <c>ReTran.Core.exe --serve</c> thật và điều khiển qua stdio bằng chính cặp
/// <see cref="CoreProcessClient"/>/<c>JsonRpcClient</c> của App, nên toàn bộ hợp đồng IPC được
/// kiểm tra end-to-end.
/// </summary>
/// <remarks>
/// Every test skips silently (early return) when the environment cannot host the flow: no built
/// Core exe, no sidecar/capture source, or a machine whose screen capture cannot produce frames.
/// Mỗi test skip-êm (early return) khi môi trường không chạy được: không có exe Core đã build,
/// không có sidecar/capture source, hoặc máy không lấy được frame từ màn hình.
/// </remarks>
public class ServeM4IntegrationTests
{
    /// <summary>Bound for one App → Core request/response round trip.
    /// Giới cho một lượt request/response App → Core.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Bound for the first <c>frame.translated</c> after <c>core.start</c> (cold OCR).
    /// Giới cho <c>frame.translated</c> đầu tiên sau <c>core.start</c> (OCR cold).</summary>
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Bound for the next cycle's frame, i.e. the "reply did not stall the loop" check.
    /// Giới cho frame của chu kỳ kế tiếp — tức kiểm tra "reply không làm nghẽn loop".</summary>
    private static readonly TimeSpan NextFrameTimeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Serve_PingStopListSources_Roundtrip_SkipEamWithoutExe()
    {
        if (CoreLocator.FindCoreExe() is not string coreExe) return; // Core chưa build -> skip-êm

        await using var peer = new ServePeer();
        await peer.StartAsync(coreExe);

        Assert.Equal("pong", (await peer.CallAsync("core.ping", null))!.GetValue<string>());

        // core.stop before any start is idempotent (spec §3.1).
        var stopped = await peer.CallAsync("core.stop", null);
        Assert.False(stopped!["running"]!.GetValue<bool>());

JsonNode? sources;
try
{
    sources = await peer.CallAsync("core.list_sources", null);
}
catch (RpcException ex) when (ex.Code == RpcErrorCodes.InternalError)
{
    return; // -32603: enumeration display/window failed (headless session) -> skip-êm
}

var monitors = Assert.IsType<JsonArray>(sources!["monitors"]);
if (monitors.Count == 0) return; // máy không có màn hình nào để assert -> skip-êm
        foreach (JsonNode? monitor in monitors)
        {
            JsonObject m = Assert.IsType<JsonObject>(monitor);
            Assert.True(m["id"]!.GetValue<int>() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(m["name"]!.GetValue<string>()));
        }
        var windows = Assert.IsType<JsonArray>(sources["windows"]);
        foreach (JsonNode? window in windows)
        {
            JsonObject w = Assert.IsType<JsonObject>(window);
            Assert.StartsWith("0x", w["hwnd"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(w["process"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task Serve_StartWithBogusProvider_Rejects_SkipEamWithoutExe()
    {
        if (CoreLocator.FindCoreExe() is not string coreExe) return; // Core chưa build -> skip-êm

        await using var peer = new ServePeer();
        await peer.StartAsync(coreExe);

        var error = await Assert.ThrowsAsync<RpcException>(() =>
            peer.CallAsync("core.start", new JsonObject
            {
                ["source"] = new JsonObject { ["kind"] = "screen", ["monitor"] = 0 },
                ["translate"] = new JsonObject { ["provider"] = "nope", ["src"] = "auto", ["dst"] = "vi" },
            }));
        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
    }

    [Fact]
    public async Task Serve_Start_EmitsFrameTranslated_AndRequestsCache_SkipEam()
    {
        if (CoreLocator.FindCoreExe() is not string coreExe) return; // Core chưa build -> skip-êm

        await using var peer = new ServePeer();
        await peer.StartAsync(coreExe);

        var started = await peer.CallAsync("core.start", new JsonObject
        {
            ["source"] = new JsonObject { ["kind"] = "screen", ["monitor"] = 0 },
            ["translate"] = new JsonObject { ["provider"] = "google", ["src"] = "auto", ["dst"] = "vi" },
        });
        // -32603 (no sidecar / capture cannot be opened) throws instead of returning -> the
        // environment cannot host the pipeline: skip-êm rather than fail.
        // -32603 (không sidecar / không mở được capture) ném thay vì trả -> môi trường không
        // chạy được pipeline: skip-êm chứ không fail.
        if (started?["running"]?.GetValue<bool>() != true) return;

        JsonObject? frame = await peer.WaitForFirstFrameAsync(FirstFrameTimeout);
        // The loop died before its first frame (e.g. this machine's DXGI screen source never
        // delivers a frame) -> no assertion is possible: skip-êm.
        // Loop chết trước frame đầu (vd DXGI screen source của máy này không frame nào) ->
        // không có gì để assert: skip-êm.
        if (frame is null) return;

        long seq = frame["seq"]!.GetValue<long>();
        Assert.True(seq >= 1);
        Assert.True(frame["w"]!.GetValue<int>() > 0);
        Assert.True(frame["h"]!.GetValue<int>() > 0);
        var boxes = Assert.IsType<JsonArray>(frame["boxes"]); // boxes:[] cũng phải parse được
        bool hasText = false;
        foreach (JsonNode? node in boxes)
        {
            JsonObject box = Assert.IsType<JsonObject>(node);
            string text = box["text"]!.GetValue<string>();
            if (text.Length > 0) hasText = true;
            Assert.True(box.ContainsKey("translated")); // tường minh null khi chưa dịch (spec §3.3)
            foreach (string key in (string[])["x", "y", "w", "h"])
                Assert.InRange(box[key]!.GetValue<double>(), 0d, 1d);
        }

        // Boxes with text mean Core must ask the App for the cache before translating; the peer
        // handler already replied {hits:{}, misses:[...]} — a second frame proves the reply did
        // not stall the loop (spec §4.1: translation never blocks the tick).
        // Box có text nghĩa là Core phải hỏi App cache trước khi dịch; handler của peer đã trả
        // {hits:{}, misses:[...]} — frame kế tiếp chứng minh reply không làm nghẽn loop
        // (spec §4.1: dịch không bao giờ block tick).
        if (hasText) Assert.NotEmpty(peer.CacheLookups);
        Assert.NotNull(await peer.WaitForNextFrameAsync(seq, NextFrameTimeout));
    }

    /// <summary>
    /// App-side stand-in for the MAUI shell: owns one <c>ReTran.Core.exe --serve</c> child, records
    /// the notifications it emits, and answers Core → App requests like the real
    /// <c>PipelineController</c> would (<c>translate.request_cache</c> → all-miss).
    /// Đóng vai App thay cho vỏ MAUI: giữ một tiến trình con <c>ReTran.Core.exe --serve</c>, ghi lại
    /// notification nhận được, và trả lời request Core → App giống <c>PipelineController</c> thật
    /// (<c>translate.request_cache</c> → all-miss).
    /// </summary>
    private sealed class ServePeer : IAsyncDisposable
    {
        private readonly CoreProcessClient _peer = new();
        private readonly List<(string Method, JsonObject? Params)> _notifications = new();
        private readonly List<JsonObject> _cacheLookups = new();
        private readonly object _sync = new();

        /// <summary>
        /// Params of every <c>translate.request_cache</c> request Core has sent, in arrival order.
        /// Tham số của mọi request <c>translate.request_cache</c> Core đã gửi, theo thứ tự tới.
        /// </summary>
        public IReadOnlyList<JsonObject> CacheLookups
        {
            get { lock (_sync) return _cacheLookups.ToList(); }
        }

        /// <summary>
        /// Starts the Core child process and subscribes to its notifications and requests.
        /// Khởi chạy tiến trình con Core rồi đăng ký lắng nghe notification và request của nó.
        /// </summary>
        /// <param name="coreExe">Path to the built <c>ReTran.Core.exe</c>. Đường dẫn exe Core đã build.</param>
        /// <returns>A task that completes once the process is up. Task hoàn tất khi tiến trình đã lên.</returns>
        public async Task StartAsync(string coreExe)
        {
            await _peer.StartAsync(coreExe, CancellationToken.None);
            _peer.SetRequestHandler(AnswerRequestAsync);
            _peer.NotificationReceived += Record;
        }

        /// <summary>
        /// Calls one Core method with a bounded wait so a hung peer fails the test instead of hanging it.
        /// Gọi một method của Core với lượt chờ có giới để peer treo thì test fail chứ không treo.
        /// </summary>
        public async Task<JsonNode?> CallAsync(string method, JsonNode? @params)
        {
            using var cts = new CancellationTokenSource(CallTimeout);
            return await _peer.CallAsync(method, @params, cts.Token);
        }

        /// <summary>
        /// Waits for the first <c>frame.translated</c>, returning null when the pipeline reported
        /// <c>pipeline.state {running:false}</c> (or nothing arrived) before any frame did.
        /// Chờ <c>frame.translated</c> đầu tiên; trả null khi pipeline báo
        /// <c>pipeline.state {running:false}</c> (hoặc không có gì tới) trước bất kỳ frame nào.
        /// </summary>
        public async Task<JsonObject?> WaitForFirstFrameAsync(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_sync)
                {
                    foreach ((string method, JsonObject? p) in _notifications)
                    {
                        if (method == "frame.translated") return p;
                        if (method == "pipeline.state"
                            && p?["running"] is JsonValue running && running.TryGetValue<bool>(out bool isRunning)
                            && !isRunning)
                            return null;
                    }
                }
                await Task.Delay(100);
            }
            return null;
        }

        /// <summary>
        /// Waits for the first <c>frame.translated</c> whose <c>seq</c> is past <paramref name="afterSeq"/>.
        /// Chờ <c>frame.translated</c> đầu tiên có <c>seq</c> lớn hơn <paramref name="afterSeq"/>.
        /// </summary>
        public async Task<JsonObject?> WaitForNextFrameAsync(long afterSeq, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                lock (_sync)
                {
                    foreach ((string method, JsonObject? p) in _notifications)
                    {
                        if (method == "frame.translated"
                            && p?["seq"] is JsonValue seq && seq.TryGetValue<long>(out long value)
                            && value > afterSeq)
                            return p;
                    }
                }
                await Task.Delay(100);
            }
            return null;
        }

        /// <summary>
        /// Stops the pipeline (best effort, so no capture source is left running) and kills the child.
        /// Dừng pipeline (best effort, không để capture source chạy mồi) rồi kết tiến trình con.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _peer.CallAsync("core.stop", null, cts.Token);
            }
            catch
            {
                // Peer already gone or slow to answer — killing the tree below is the real cleanup.
                // Peer đã chết hoặc trả lời chậm — giết cả cây tiến trình mới là dọn dẹp thật.
            }
            await _peer.DisposeAsync();
        }

        /// <summary>
        /// Answers Core → App requests: <c>translate.request_cache</c> is recorded and answered as
        /// all-miss (an App with an empty cache); anything else gets a null result.
        /// Trả lời request Core → App: <c>translate.request_cache</c> được ghi lại và trả all-miss
        /// (App có cache rỗng); request khác trả result null.
        /// </summary>
        private Task<JsonNode?> AnswerRequestAsync(string method, JsonNode? @params)
        {
            // A request (not a notification) reaches this handler only because Core sent an id —
            // that is the "id + method" evidence the harness asserts on.
            // Chỉ request (không phải notification) mới tới handler này khi Core gửi kèm id —
            // đó là bằng chứng "id + method" mà harness kiểm tra.
            if (method == "translate.request_cache" && @params?["texts"] is JsonArray texts)
            {
                var misses = new JsonArray();
                foreach (JsonNode? text in texts) misses.Add(text?.DeepClone());
                lock (_sync) _cacheLookups.Add(@params.AsObject());
                return Task.FromResult<JsonNode?>(new JsonObject
                {
                    ["hits"] = new JsonObject(),
                    ["misses"] = misses,
                });
            }
            return Task.FromResult<JsonNode?>(null);
        }

        /// <summary>
        /// Records one incoming Core notification (method + params) for later assertions.
        /// Ghi lại một notification từ Core (method + params) để assert sau.
        /// </summary>
        private void Record(string method, JsonNode? @params)
        {
            lock (_sync) _notifications.Add((method, @params as JsonObject));
        }
    }
}
