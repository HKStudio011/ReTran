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
/// xUnit v2 cannot skip a test dynamically, so every environment-driven early return writes a
/// <c>SKIPPED (env): …</c> line through <see cref="ITestOutputHelper"/> before returning — a run
/// that asserts nothing is visible instead of silent. A wait that times out while the pipeline is
/// still alive is NOT an environment skip: it fails.
/// xUnit v2 không skip động được, nên mọi nhánh early return vì môi trường in một dòng
/// <c>SKIPPED (env): …</c> qua <see cref="ITestOutputHelper"/> rồi mới return — lần chạy assert
/// 0 lần sẽ thấy được thay vì im lặng. Lượt chờ hết giờ trong khi pipeline vẫn sống KHÔNG phải
/// skip môi trường: test fail.
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
    private static readonly TimeSpan NextFrameTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Bound for Core to ask the App for the cache after the first frame (spec §4.2).
    /// Giới cho Core hỏi App cache sau frame đầu (spec §4.2).</summary>
    private static readonly TimeSpan CacheLookupTimeout = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _output;

    /// <summary>
    /// Creates the fixture and captures the output helper used to report environment skips.
    /// Tạo fixture và lấy output helper dùng để báo các nhánh skip vì môi trường.
    /// </summary>
    /// <param name="output">xUnit output helper. Output helper của xUnit.</param>
    public ServeM4IntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Serve_PingStopListSources_Roundtrip_SkipEamWithoutExe()
    {
        if (CoreLocator.FindCoreExe() is not string coreExe)
        {
            SkipEnv("ReTran.Core.exe not built/RETRAN_CORE_EXE unset");
            return;
        }

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
            SkipEnv($"core.list_sources returned -32603 (display/window enumeration failed): {ex.Message}");
            return;
        }

        var monitors = Assert.IsType<JsonArray>(sources!["monitors"]);
        if (monitors.Count == 0)
        {
            // Only the monitor half is unassertable here — the window list is still asserted below.
            // Chỉ nửa monitor không assert được — danh sách cửa sổ vẫn assert ở dưới.
            _output.WriteLine("SKIPPED (env): core.list_sources returned no monitors — monitor assertions skipped");
        }
        else
        {
            foreach (JsonNode? monitor in monitors)
            {
                JsonObject m = Assert.IsType<JsonObject>(monitor);
                Assert.True(m["id"]!.GetValue<int>() >= 0);
                Assert.False(string.IsNullOrWhiteSpace(m["name"]!.GetValue<string>()));
            }
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
        if (CoreLocator.FindCoreExe() is not string coreExe)
        {
            SkipEnv("ReTran.Core.exe not built/RETRAN_CORE_EXE unset");
            return;
        }

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
        if (CoreLocator.FindCoreExe() is not string coreExe)
        {
            SkipEnv("ReTran.Core.exe not built/RETRAN_CORE_EXE unset");
            return;
        }

        await using var peer = new ServePeer();
        await peer.StartAsync(coreExe);

        JsonNode? started;
        try
        {
            started = await peer.CallAsync("core.start", new JsonObject
            {
                ["source"] = new JsonObject { ["kind"] = "screen", ["monitor"] = 0 },
                ["translate"] = new JsonObject { ["provider"] = "google", ["src"] = "auto", ["dst"] = "vi" },
            });
        }
        catch (RpcException ex) when (ex.Code == RpcErrorCodes.InternalError)
        {
            SkipEnv($"core.start -32603 (no sidecar / capture source unavailable): {ex.Message}");
            return;
        }

        // A start that answered without running:true breaks the spec §3.1 contract — it is a hard
        // assertion, not an environment skip.
        // Start trả về mà không running:true là vi phạm hợp đồng spec §3.1 — assert cứng, không
        // phải skip môi trường.
        Assert.True(started?["running"]?.GetValue<bool>() == true,
            $"core.start returned {started?.ToJsonString() ?? "null"} instead of {{\"running\":true}}");

        FrameWaitResult first = await peer.WaitForFirstFrameAsync(FirstFrameTimeout);
        if (first.Outcome == FrameWaitOutcome.EnvironmentStopped)
        {
            SkipEnv(first.Detail);
            return;
        }
        Assert.True(first.Outcome == FrameWaitOutcome.Received, first.Detail);
        JsonObject frame = first.Frame!;

        long seq = frame["seq"]!.GetValue<long>();
        Assert.True(seq >= 1);
        Assert.True(frame["w"]!.GetValue<int>() > 0);
        Assert.True(frame["h"]!.GetValue<int>() > 0);
        var boxes = Assert.IsType<JsonArray>(frame["boxes"]); // boxes:[] cũng phải parse được
        var observedTexts = new List<string>();
        foreach (JsonNode? node in boxes)
        {
            JsonObject box = Assert.IsType<JsonObject>(node);
            string text = box["text"]!.GetValue<string>();
            // Core drops whitespace-only texts (OcrDiff / TranslateCycleSink use IsNullOrWhiteSpace),
            // so only texts that survive that filter can appear in the cache batch.
            // Core loại text chỉ khoảng trắng (OcrDiff / TranslateCycleSink dùng IsNullOrWhiteSpace),
            // nên chỉ text qua bộ lọc đó mới có thể xuất hiện trong lô cache.
            if (!string.IsNullOrWhiteSpace(text)) observedTexts.Add(text);
            Assert.True(box.ContainsKey("translated")); // tường minh null khi chưa dịch (spec §3.3)
            foreach (string key in (string[])["x", "y", "w", "h"])
                Assert.InRange(box[key]!.GetValue<double>(), 0d, 1d);
        }

        // Boxes with text mean Core must ask the App for the cache before translating. The lookup
        // is a background hop that can lag the frame it follows, so wait for it with its own bound
        // instead of sampling once; then assert the batch shape and that our all-miss reply carried
        // a text we actually saw on the frame.
        // Box có text nghĩa là Core phải hỏi App cache trước khi dịch. Lượt tra là hop nền có thể
        // trễ sau frame, nên chờ bằng giới riêng thay vì lấy một lần; rồi assert shape của lô và
        // rằng reply all-miss có chứa text ta thật sự thấy trên frame.
        if (observedTexts.Count > 0)
        {
            IReadOnlyList<CacheLookup> lookups = await peer.WaitForCacheLookupsAsync(1, CacheLookupTimeout);
            Assert.True(lookups.Count > 0,
                $"no translate.request_cache within {CacheLookupTimeout.TotalSeconds:0}s although the frame carried "
                + $"{observedTexts.Count} non-empty text(s)");
            foreach (CacheLookup lookup in lookups)
            {
                Assert.False(string.IsNullOrWhiteSpace(ReadString(lookup.Request["context"])));
                Assert.Equal("auto", ReadString(lookup.Request["src"]));
                Assert.Equal("vi", ReadString(lookup.Request["dst"]));
                Assert.NotEmpty(Assert.IsType<JsonArray>(lookup.Request["texts"]));
            }
            Assert.Contains(observedTexts[0], lookups.SelectMany(l => l.Misses).Select(ReadString));
        }

        // A following frame proves the cache reply did not stall the loop (spec §4.1: translation
        // never blocks the tick).
        // Frame kế tiếp chứng minh reply cache không làm nghẽn loop (spec §4.1: dịch không bao giờ
        // block tick).
        FrameWaitResult next = await peer.WaitForNextFrameAsync(seq, NextFrameTimeout);
        if (next.Outcome == FrameWaitOutcome.EnvironmentStopped)
        {
            // The frame assertions above already ran, so this is a partial run: a neutral marker,
            // not the SKIPPED (env) marker that means "asserted nothing".
            // Các assert frame ở trên đã chạy, nên đây là lần chạy một phần: dùng marker trung tính,
            // không dùng marker SKIPPED (env) vốn mang nghĩa "assert 0 lần".
            _output.WriteLine($"ENV NOTE: {next.Detail}");
            return;
        }
        Assert.True(next.Outcome == FrameWaitOutcome.Received, next.Detail);
    }

    /// <summary>
    /// Records an environment-driven early return through the output helper, so a run that asserted
    /// nothing still leaves a visible <c>SKIPPED (env): …</c> line in the test output.
    /// Ghi một nhánh early return vì môi trường qua output helper, để lần chạy assert 0 lần vẫn
    /// để lại dòng <c>SKIPPED (env): …</c> nhìn thấy được trong output của test.
    /// </summary>
    /// <param name="reason">Why the environment cannot host this assertion. Vì sao môi trường không chạy được assertion này.</param>
    private void SkipEnv(string reason) => _output.WriteLine($"SKIPPED (env): {reason}");

    /// <summary>
    /// Reads a JSON node as a string, yielding an empty string when it is absent or not a string.
    /// Đọc node JSON thành chuỗi, trả chuỗi rỗng khi thiếu hoặc không phải string.
    /// </summary>
    private static string ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) && text is not null ? text : string.Empty;

    /// <summary>How a wait for a <c>frame.translated</c> ended. Kết quả chờ <c>frame.translated</c>.</summary>
    private enum FrameWaitOutcome
    {
        /// <summary>The awaited frame arrived. Frame mong đợi đã tới.</summary>
        Received,

        /// <summary>
        /// The pipeline reported <c>pipeline.state {running:false}</c> with an environment reason
        /// (<c>capture-error</c> / <c>sidecar-exited</c>) — the machine cannot produce frames.
        /// Skip, not a regression.
        /// Pipeline báo <c>pipeline.state {running:false}</c> với lý do môi trường
        /// (<c>capture-error</c> / <c>sidecar-exited</c>) — máy không lấy được frame. Skip, không
        /// phải regression.
        /// </summary>
        EnvironmentStopped,

        /// <summary>
        /// The pipeline reported <c>pipeline.state {running:false}</c> with <c>internal</c> or any
        /// unknown reason — the loop died on its own, which is a crash, not an environment limit.
        /// Must fail.
        /// Pipeline báo <c>pipeline.state {running:false}</c> với <c>internal</c> hoặc lý do lạ —
        /// loop tự chết, đây là crash chứ không phải giới hạn môi trường. Phải fail.
        /// </summary>
        PipelineCrashed,

        /// <summary>
        /// The bound elapsed with no frame and no stop signal — the pipeline is alive but not
        /// emitting, which is a real regression. Hết giờ mà không có frame lẫn tín hiệu dừng —
        /// pipeline còn sống nhưng không emit: đây là regression thật.
        /// </summary>
        TimedOut,
    }

    /// <summary>Outcome of a frame wait plus the frame (when received) and a diagnostic detail.
    /// Kết quả chờ frame kèm frame (khi có) và một dòng chẩn đoán.</summary>
    /// <param name="Outcome">How the wait ended. Kết thúc ra sao.</param>
    /// <param name="Frame">The frame, or null. Frame hoặc null.</param>
    /// <param name="Detail">Human-readable reason, used as skip text or failure message. Lý do dạng chữ để hiện khi skip hoặc khi fail.</param>
    private sealed record FrameWaitResult(FrameWaitOutcome Outcome, JsonObject? Frame, string Detail);

    /// <summary>One recorded <c>translate.request_cache</c> call: the request and the misses we replied.
    /// Một lượt <c>translate.request_cache</c> đã ghi: request và các miss đã trả.</summary>
    /// <param name="Request">Params Core sent. Params Core gửi.</param>
    /// <param name="Misses">Texts answered as cache misses. Các text được trả về là cache miss.</param>
    private sealed record CacheLookup(JsonObject Request, JsonArray Misses);

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
        private readonly List<CacheLookup> _cacheLookups = new();
        private readonly object _sync = new();

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
        /// Waits for the first <c>frame.translated</c>. Returns
        /// <see cref="FrameWaitOutcome.EnvironmentStopped"/> when <c>pipeline.state {running:false}</c>
        /// arrives first with an environment reason, <see cref="FrameWaitOutcome.PipelineCrashed"/>
        /// when it arrives with <c>internal</c>/unknown, and <see cref="FrameWaitOutcome.TimedOut"/>
        /// when the bound elapses with the pipeline still alive — the last two are real regressions
        /// the caller must fail on.
        /// Chờ <c>frame.translated</c> đầu tiên. Trả
        /// <see cref="FrameWaitOutcome.EnvironmentStopped"/> khi <c>pipeline.state {running:false}</c>
        /// tới trước với lý do môi trường, <see cref="FrameWaitOutcome.PipelineCrashed"/> khi tới
        /// với <c>internal</c>/lý do lạ, và <see cref="FrameWaitOutcome.TimedOut"/> khi hết giờ mà
        /// pipeline vẫn sống — hai cái sau là regression thật, caller phải fail.
        /// </summary>
        public Task<FrameWaitResult> WaitForFirstFrameAsync(TimeSpan timeout) =>
            WaitForFrameAsync(timeout, null);

        /// <summary>
        /// Waits for the next <c>frame.translated</c>, i.e. one whose <c>seq</c> is past
        /// <paramref name="afterSeq"/>, with the same stop/timeout semantics as
        /// <see cref="WaitForFirstFrameAsync"/>.
        /// Chờ <c>frame.translated</c> kế tiếp (seq lớn hơn <paramref name="afterSeq"/>), cùng ngữ
        /// nghĩa stop/timeout như <see cref="WaitForFirstFrameAsync"/>.
        /// </summary>
        public Task<FrameWaitResult> WaitForNextFrameAsync(long afterSeq, TimeSpan timeout) =>
            WaitForFrameAsync(timeout, afterSeq);

        /// <summary>
        /// Waits until Core has asked for the cache at least <paramref name="count"/> times.
        /// Chờ tới khi Core hỏi cache ít nhất <paramref name="count"/> lần.
        /// </summary>
        public async Task<IReadOnlyList<CacheLookup>> WaitForCacheLookupsAsync(int count, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                IReadOnlyList<CacheLookup> snapshot = CacheLookups();
                if (snapshot.Count >= count) return snapshot;
                if (sw.Elapsed >= timeout) return snapshot;
                await Task.Delay(100);
            }
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
        /// Polls recorded notifications until a matching frame arrives, the pipeline reports it
        /// stopped, or the bound elapses. <paramref name="afterSeq"/> of null means "any frame".
        /// Quét notification đã ghi tới khi có frame khớp, pipeline báo đã dừng, hoặc hết giờ.
        /// <paramref name="afterSeq"/> null nghĩa là "bất kỳ frame nào".
        /// </summary>
        private async Task<FrameWaitResult> WaitForFrameAsync(TimeSpan timeout, long? afterSeq)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (_sync)
                {
                    foreach ((string method, JsonObject? p) in _notifications)
                    {
                        if (method == "pipeline.state"
                            && p?["running"] is JsonValue running && running.TryGetValue<bool>(out bool isRunning)
                            && !isRunning)
                        {
                            string reason = ReadString(p["reason"]);
                            // Only a dead capture source or a dead sidecar is an environment limit;
                            // reason "internal" (loop crashed on an exception) must fail the test.
                            // Chỉ capture source chết hoặc sidecar chết mới là giới hạn môi trường;
                            // reason "internal" (loop crash do exception) phải làm test fail.
                            bool environment = reason is "capture-error" or "sidecar-exited";
                            return new FrameWaitResult(
                                environment ? FrameWaitOutcome.EnvironmentStopped : FrameWaitOutcome.PipelineCrashed,
                                null,
                                environment
                                    ? $"pipeline.state {{running:false, reason:\"{reason}\"}} before any frame "
                                      + "— the capture source or sidecar died in this environment"
                                    : $"pipeline.state {{running:false, reason:\"{reason}\"}} before any frame "
                                      + "— the pipeline loop died on its own (crash, not an environment limit)");
                        }
                        if (method != "frame.translated") continue;
                        if (afterSeq is null) return new FrameWaitResult(FrameWaitOutcome.Received, p, "");
                        if (p?["seq"] is JsonValue seq && seq.TryGetValue<long>(out long value) && value > afterSeq.Value)
                            return new FrameWaitResult(FrameWaitOutcome.Received, p, "");
                    }
                }
                if (sw.Elapsed >= timeout)
                {
                    return new FrameWaitResult(FrameWaitOutcome.TimedOut, null,
                        $"no frame.translated within {timeout.TotalSeconds:0}s while the pipeline was still alive "
                        + $"(no pipeline.state stop seen); notifications observed: {NotificationSummary()}");
                }
                await Task.Delay(100);
            }
        }

        /// <summary>
        /// Snapshot of the recorded <c>translate.request_cache</c> calls.
        /// Ảnh chụp các lượt <c>translate.request_cache</c> đã ghi.
        /// </summary>
        private IReadOnlyList<CacheLookup> CacheLookups()
        {
            lock (_sync) return _cacheLookups.ToList();
        }

        /// <summary>
        /// Compact "method=count" summary of the recorded notifications, for failure messages.
        /// Tóm tắt "method=count" của notification đã ghi, dùng cho message khi fail.
        /// </summary>
        private string NotificationSummary()
        {
            lock (_sync)
            {
                return _notifications.Count == 0
                    ? "(none)"
                    : string.Join(", ", _notifications
                        .GroupBy(n => n.Method)
                        .Select(g => $"{g.Key}={g.Count()}"));
            }
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
                lock (_sync) _cacheLookups.Add(new CacheLookup(@params.AsObject(), misses));
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
