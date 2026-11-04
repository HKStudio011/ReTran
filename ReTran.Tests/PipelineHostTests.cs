using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.JsonRpc;
using ReTran.Core.OcrClient;
using ReTran.Core.Serve;
using ReTran.Core.Translate;
using Xunit;
using AppPreview = ReTran_App.Services.Preview;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class PipelineHostTests
{
    private sealed class FakeSidecarRpc : ISidecarRpc
    {
        public JsonNode? NextResult { get; set; }
        public Exception? Throw { get; set; }

        /// <summary>
        /// Per-method result overrides, consulted before <see cref="NextResult"/>; lets one fake
        /// answer <c>ocr.models.list</c> with a catalog while spots return a spot result.
        /// Kết quả ghi đè theo method, tra trước <see cref="NextResult"/>; cho phép một fake
        /// trả <c>ocr.models.list</c> bằng catalog trong khi spot trả kết quả spot.
        /// </summary>
        public Dictionary<string, JsonNode?> ResultsByMethod { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Per-method exceptions thrown after the call is recorded; lets a test fail only
        /// <c>ocr.spot</c> (the warmup) without breaking the rest.
        /// Ngoại lệ theo method, ném sau khi call đã được ghi; cho test hỏng riêng
        /// <c>ocr.spot</c> (warmup) mà không ảnh hưởng phần còn lại.
        /// </summary>
        public Dictionary<string, Exception> ThrowByMethod { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Every call in arrival order (method + params), written from the loop, the warmup and
        /// the test thread, read while they run.
        /// Mọi lời gọi theo thứ tự đến (method + params), ghi từ loop, warmup và luồng
        /// test, đọc trong khi chúng chạy.
        /// </summary>
        public ConcurrentQueue<(string Method, JsonNode? Params)> Calls { get; } = new();

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Enqueue((method, @params));
            if (ThrowByMethod.TryGetValue(method, out Exception? perMethod)) throw perMethod;
            if (Throw is not null) throw Throw;
            return Task.FromResult(ResultsByMethod.TryGetValue(method, out JsonNode? result) ? result : NextResult);
        }
    }

    /// <summary>Translator stub counting calls per session (Name matches the registry key used by core.start).</summary>
    private sealed class CountingTranslator : ITranslator
    {
        private int _calls;

        public string Name => "google";
        public int Calls => Volatile.Read(ref _calls);
        public void Reset() => Interlocked.Exchange(ref _calls, 0);

        public Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult("vi:" + text);
        }
    }

    /// <summary>Annotator stub returning fixed JPEG bytes so tests can assert the exact payload.</summary>
    private sealed class FakeAnnotator : IFrameAnnotator
    {
        public byte[] Result { get; set; } = [1, 2, 3];

        /// <summary>Output size the fake reports; defaults to TinyFrame's 2×2. Kích thước fake báo ra; mặc định 2×2 như TinyFrame.</summary>
        public int Width { get; set; } = 2;
        public int Height { get; set; } = 2;

        public AnnotatedFrame Annotate(Frame frame, AnnotSnapshot? snapshot) =>
            new(Result, Width, Height);
    }

    /// <summary>
    /// Long-lived JSON-RPC transport: blocking read channel, captured writes, and an auto-responder
    /// that answers translate.request_cache with an all-miss result (no real App in tests).
    /// Transport JSON-RPC dài hạn: kênh đọc blocking, ghi có capture, và auto-responder trả
    /// translate.request_cache dạng all-miss (test không cần App thật).
    /// </summary>
    private sealed class PeerTransport : IJsonRpcTransport
    {
        private readonly Channel<string> _in = Channel.CreateUnbounded<string>();
        private readonly ConcurrentQueue<string> _out = new();

        public string[] OutSnapshot() => [.. _out];

        public async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            while (await _in.Reader.WaitToReadAsync(ct))
            {
                if (_in.Reader.TryRead(out string? line)) return line;
            }
            return null;
        }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            _out.Enqueue(line);
            AutoRespond(line);
            return Task.CompletedTask;
        }

        public void Complete() => _in.Writer.TryComplete();

        private void AutoRespond(string line)
        {
            JsonNode? node = JsonRpc.ParseLine(line);
            if (node?["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out string? id))
                return; // notification (no id) -> nothing to answer
            if (node?["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out string? method))
                return; // a response line, not a request

            if (method == "translate.request_cache")
            {
                var result = new JsonObject
                {
                    ["hits"] = new JsonObject(),
                    ["misses"] = node!["params"]?["texts"] is JsonArray texts
                        ? texts.DeepClone()
                        : new JsonArray(),
                };
                _in.Writer.TryWrite(JsonRpc.SerializeResponse(new JsonRpcResponse(id, result, null)));
            }
            else
            {
                _in.Writer.TryWrite(JsonRpc.SerializeResponse(new JsonRpcResponse(id, null,
                    new JsonRpcError(RpcErrorCodes.MethodNotFound, $"Method not found: {method}"))));
            }
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        public PeerTransport Transport { get; } = new();
        public JsonRpcServer Server { get; } = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Peer() => _run = Server.RunAsync(Transport, _cts.Token);

        public async ValueTask DisposeAsync()
        {
            Transport.Complete();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* read loop may fault on cancellation — test end */ }
            _cts.Dispose();
        }
    }

    private static JsonObject StartParams(
        string kind = "screen",
        int monitor = 0,
        string? hwnd = null,
        string provider = "google",
        string src = "auto",
        string dst = "vi")
    {
        var source = new JsonObject { ["kind"] = kind };
        if (hwnd is not null) source["hwnd"] = hwnd;
        else source["monitor"] = monitor;
        return new JsonObject
        {
            ["source"] = source,
            ["translate"] = new JsonObject { ["provider"] = provider, ["src"] = src, ["dst"] = dst },
            ["ocrFps"] = 10,
            ["captureFps"] = 10,
        };
    }

    private static TranslatorRegistry RegistryWith(ITranslator translator)
    {
        var registry = new TranslatorRegistry();
        registry.Register(translator);
        return registry;
    }

    /// <summary>Fresh preview shared-memory name per test so parallel runs never collide. Tên shared memory preview mới cho mỗi test để chạy song song không đụng nhau.</summary>
    private static string UniquePreviewMapName(string stem) => $@"Local\ReTran.Preview.Test.{stem}.{Guid.NewGuid():N}";

    private static Frame TinyFrame() => Frame.FromBgra(2, 2, new byte[16]);

    private static JsonObject SpotResult(params string[] texts)
    {
        var spots = new JsonArray();
        foreach (string t in texts)
        {
            spots.Add(new JsonObject
            {
                ["box"] = new JsonArray(
                    new JsonArray(0, 0), new JsonArray(1, 0),
                    new JsonArray(1, 1), new JsonArray(0, 1)),
                ["text"] = t,
                ["score"] = 0.9,
            });
        }
        return new JsonObject { ["spots"] = spots, ["elapsed_ms"] = 5 };
    }

    /// <summary>
    /// Builds an <c>ocr.models.list</c> reply with one catalog entry per model (only the
    /// <c>name</c>/<c>installed</c> fields the core's installed check reads).
    /// Dựng phản hồi <c>ocr.models.list</c> với một mục catalog mỗi model (chỉ các field
    /// <c>name</c>/<c>installed</c> mà bước kiểm tra installed của core đọc).
    /// </summary>
    private static JsonObject CatalogResult(params (string Name, bool Installed)[] models)
    {
        var catalog = new JsonArray();
        foreach ((string name, bool installed) in models)
        {
            catalog.Add(new JsonObject
            {
                ["name"] = name,
                ["kind"] = name.StartsWith("det", StringComparison.Ordinal) ? "det" : "rec",
                ["installed"] = installed,
                ["inUse"] = false,
            });
        }
        return new JsonObject { ["catalog"] = catalog };
    }

    /// <summary>
    /// Builds a host; when <paramref name="sources"/> is given, every start opens a fresh fake
    /// source with one frame queued (flushed at Start, consumed by the first-frame wait).
    /// Tạo host; khi truyền <paramref name="sources"/>, mỗi lần start mở source giả mới với 1 frame
    /// xếp hàng sẵn (phát lúc Start, được bước chờ frame đầu tiêu thụ).
    /// </summary>
    private static PipelineHost CreateHost(
        ISidecarRpc? rpc,
        TranslatorRegistry registry,
        JsonRpcServer server,
        List<FakeCaptureSource>? sources = null,
        TextWriter? stderr = null,
        Func<OcrLoopOptions, ICaptureSource>? sourceFactory = null,
        IFrameAnnotator? annotator = null,
        string? previewMapName = null)
    {
        Func<OcrLoopOptions, ICaptureSource>? factory = sourceFactory;
        if (factory is null && sources is not null)
        {
            factory = _ =>
            {
                var source = new FakeCaptureSource();
                source.Raise(TinyFrame());
                sources.Add(source);
                return source;
            };
        }
        return new PipelineHost(rpc, registry, server, stderr ?? new StringWriter(), factory, annotator,
            previewMapName: previewMapName);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for {what}");
    }

    private static async Task<JsonNode?> WaitForNotificationAsync(Peer peer, string method)
    {
        JsonNode? found = null;
        await WaitUntilAsync(() =>
        {
            foreach (string line in peer.Transport.OutSnapshot())
            {
                JsonNode? node = JsonRpc.ParseLine(line);
                if (node?["id"] is null
                    && node?["method"] is JsonValue methodValue
                    && methodValue.TryGetValue<string>(out string? m)
                    && m == method)
                {
                    found = node["params"];
                    return true;
                }
            }
            return false;
        }, $"notification {method}");
        return found;
    }

    /// <summary>
    /// Collects every <paramref name="method"/> notification currently on the wire (counting all,
    /// not just the first) so "exactly once" / "none" can be asserted.
    /// Thu mọi thông báo <paramref name="method"/> đang có trên wire (đếm tất cả, không chỉ cái
    /// đầu) để assert được "đúng 1 lần" / "không có".
    /// </summary>
    private static List<JsonNode?> NotificationsOf(Peer peer, string method)
    {
        var found = new List<JsonNode?>();
        foreach (string line in peer.Transport.OutSnapshot())
        {
            JsonNode? node = JsonRpc.ParseLine(line);
            if (node?["id"] is null
                && node?["method"] is JsonValue methodValue
                && methodValue.TryGetValue<string>(out string? m)
                && m == method)
            {
                found.Add(node["params"]);
            }
        }
        return found;
    }

    [Fact]
    public async Task Start_InvalidSourceKind_InvalidParams()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(StartParams(kind: "bogus"), CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_UnknownProvider_InvalidParams()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(StartParams(provider: "deepl"), CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Start_NoSidecar_InternalError()
    {
        await using var host = CreateHost(rpc: null, RegistryWith(new CountingTranslator()), new JsonRpcServer());
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(StartParams(), CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("sidecar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_WhileRunning_InvalidRequest()
    {
        var sources = new List<FakeCaptureSource>();
        await using var host = CreateHost(
            new FakeSidecarRpc { NextResult = SpotResult() },
            RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(StartParams(), CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidRequest, ex.Code);
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stop_BeforeStart_ReturnsFalse()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());
        JsonNode? result = await host.StopAsync(null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.False(result!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ListSources_ReturnsMonitorsAndWindowsShape()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());
        JsonNode? result = await host.ListSourcesAsync(CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result!["monitors"]);
        Assert.NotNull(result["windows"]);

        JsonArray monitors = result["monitors"]!.AsArray();
        if (monitors.Count == 0) return; // headless env: shape verified for monitors only when present
        foreach (JsonNode? m in monitors)
        {
            Assert.NotNull(m!["id"]);
            Assert.Equal(JsonValueKind.Number, m!["id"]!.GetValueKind());
            Assert.False(string.IsNullOrEmpty(m!["name"]!.GetValue<string>()));
        }
        foreach (JsonNode? w in result["windows"]!.AsArray())
        {
            Assert.StartsWith("0x", w!["hwnd"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(w["title"]);
            Assert.NotNull(w["process"]);
        }
    }

    [Fact]
    public async Task SidecarLost_NotifiesPipelineState()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { Throw = new SidecarRpcException(-32603, "sidecar exited") };
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), peer.Server, sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        await Task.Delay(400, TestContext.Current.CancellationToken); // queued frame consumed by the first-frame wait
        sources[^1].Raise(TinyFrame()); // next tick -> SpotAsync -> SidecarRpcException -> fatal

        JsonNode? state = await WaitForNotificationAsync(peer, "pipeline.state");
        Assert.False(state!["running"]!.GetValue<bool>());
        Assert.Equal("sidecar-exited", state["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Restart_FreshSession_TranslatesAgain()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        var translator = new CountingTranslator();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult("HELLO") };
        await using var host = CreateHost(rpc, RegistryWith(translator), peer.Server, sources);

        JsonNode? first = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(first!["running"]!.GetValue<bool>());
        await Task.Delay(400, TestContext.Current.CancellationToken);
        sources[^1].Raise(TinyFrame()); // cycle 1 -> new_texts ["HELLO"] -> translate
        await WaitUntilAsync(() => translator.Calls >= 1, "session-1 translation");

        JsonNode? stopped = await host.StopAsync(null, CancellationToken.None);
        Assert.False(stopped!["running"]!.GetValue<bool>());

        translator.Reset(); // fresh OcrDiff in session 2 must surface the same text as new again
        JsonNode? second = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(second!["running"]!.GetValue<bool>());
        await Task.Delay(400, TestContext.Current.CancellationToken);
        sources[^1].Raise(TinyFrame()); // cycle -> new_texts ["HELLO"] again -> translate
        await WaitUntilAsync(() => translator.Calls >= 1, "session-2 translation (fresh OcrDiff)");
    }

    [Fact]
    public async Task Stop_Normal_NoStateNotification()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        await using var host = CreateHost(
            new FakeSidecarRpc { NextResult = SpotResult() },
            RegistryWith(new CountingTranslator()), peer.Server, sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
        await Task.Delay(400, TestContext.Current.CancellationToken); // queued frame consumed by the first-frame wait; loop now ticking

        JsonNode? stopped = await host.StopAsync(null, CancellationToken.None);
        Assert.False(stopped!["running"]!.GetValue<bool>());

        await Task.Delay(700, TestContext.Current.CancellationToken); // settle window: a stray fire-and-forget notify would flush here
        Assert.Empty(NotificationsOf(peer, "pipeline.state"));
    }

    [Fact]
    public async Task FatalState_NotifiedExactlyOnce()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { Throw = new SidecarRpcException(-32603, "sidecar exited") };
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), peer.Server, sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        await Task.Delay(400, TestContext.Current.CancellationToken); // queued frame consumed by the first-frame wait
        sources[^1].Raise(TinyFrame()); // next tick -> SpotAsync -> SidecarRpcException -> fatal

        JsonNode? state = await WaitForNotificationAsync(peer, "pipeline.state");
        Assert.False(state!["running"]!.GetValue<bool>());
        await Task.Delay(500, TestContext.Current.CancellationToken); // settle window: a second (buggy) notify would land here
        JsonNode? only = Assert.Single(NotificationsOf(peer, "pipeline.state"));
        Assert.Equal("sidecar-exited", only!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_SourceFactoryThrows_RpcInternalError()
    {
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer(),
            sourceFactory: _ => throw new ArgumentOutOfRangeException("monitor", "no displays"));

        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(StartParams(), CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("capture source unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_EmitsSlimFrameAnnotated_AndWritesJpegToSharedMemory()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        string previewMapName = UniquePreviewMapName("annotated");
        await using var host = CreateHost(
            new FakeSidecarRpc { NextResult = SpotResult() },
            RegistryWith(new CountingTranslator()), peer.Server, sources,
            annotator: new FakeAnnotator(), previewMapName: previewMapName);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        JsonNode? frame = await WaitForNotificationAsync(peer, "frame.annotated");
        Assert.Equal(1, frame!["seq"]!.GetValue<long>());
        Assert.Equal(2, frame["w"]!.GetValue<int>());   // TinyFrame 2x2
        Assert.Equal(2, frame["h"]!.GetValue<int>());
        // The JPEG no longer rides the wire: the notification only says "read the section".
        // JPEG không còn đi trên wire: thông báo chỉ nói "hãy đọc vùng nhớ".
        Assert.Null(frame["jpeg"]);

        Assert.True(AppPreview.PreviewChannelReader.TryOpen(
            previewMapName, out AppPreview.PreviewChannelReader? reader));
        using (reader)
        {
            Assert.True(reader!.TryRead(out AppPreview.PreviewChannelFrame mmf));
            Assert.Equal(new byte[] { 1, 2, 3 }, mmf.Jpeg);
            Assert.Equal(2, mmf.Width);
            Assert.Equal(2, mmf.Height);
        }
    }

    [Fact]
    public async Task Stop_AnnotationLoop_StopsEmitting()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        await using var host = CreateHost(
            new FakeSidecarRpc { NextResult = SpotResult() },
            RegistryWith(new CountingTranslator()), peer.Server, sources,
            annotator: new FakeAnnotator(), previewMapName: UniquePreviewMapName("stop"));

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        // Keep frames flowing so the loop would keep emitting if it survived the stop.
        // Giữ frame chảy liên tục để loop sẽ còn emit nếu sống sót sau stop.
        using var feederCts = new CancellationTokenSource();
        Task feeder = Task.Run(async () =>
        {
            while (!feederCts.IsCancellationRequested)
            {
                if (sources.Count > 0) sources[^1].Raise(TinyFrame());
                try { await Task.Delay(50, feederCts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });

        try
        {
            await WaitUntilAsync(
                () => NotificationsOf(peer, "frame.annotated").Count >= 2,
                "two frame.annotated notifications");

            JsonNode? stopped = await host.StopAsync(null, CancellationToken.None);
            Assert.False(stopped!["running"]!.GetValue<bool>());

            int count = NotificationsOf(peer, "frame.annotated").Count;
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Equal(count, NotificationsOf(peer, "frame.annotated").Count);
        }
        finally
        {
            feederCts.Cancel();
            await feeder;
        }
    }

    [Fact]
    public async Task Start_CaptureFps_FlowsToSourceFactory()
    {
        OcrLoopOptions? captured = null;
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer(),
            sourceFactory: opts =>
            {
                captured = opts;
                var source = new FakeCaptureSource();
                source.Raise(TinyFrame());
                return source;
            });

        var parameters = StartParams();
        parameters["captureFps"] = 5;
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        Assert.NotNull(captured);
        Assert.Equal(5, captured!.CaptureFps);
    }

    [Fact]
    public async Task Start_CaptureFpsZero_InvalidParams()
    {
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var parameters = StartParams();
        parameters["captureFps"] = 0;
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("captureFps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_CaptureFps1000_InvalidParams()
    {
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var parameters = StartParams();
        parameters["captureFps"] = 1000;
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("captureFps", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_FpsMissing_UsesCoreConfigDefault()
    {
        OcrLoopOptions? captured = null;
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer(),
            sourceFactory: opts =>
            {
                captured = opts;
                var source = new FakeCaptureSource();
                source.Raise(TinyFrame());
                return source;
            });

        var parameters = new JsonObject // no ocrFps/captureFps: defaults must come from CoreConfig
        {
            ["source"] = new JsonObject { ["kind"] = "screen", ["monitor"] = 0 },
            ["translate"] = new JsonObject { ["provider"] = "google", ["src"] = "auto", ["dst"] = "vi" },
        };
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        Assert.NotNull(captured);
        Assert.Equal(CoreConfig.LoadFrom(CoreConfig.DefaultPath).OcrFps, captured!.Fps);
        Assert.True(captured.Fps >= 1);
        Assert.Equal(CoreConfig.LoadFrom(CoreConfig.DefaultPath).CaptureFps, captured!.CaptureFps);
    }

    //
    // core.start ocr:{det,rec} — installed check + pair switch before any loop work.
    // core.start ocr:{det,rec} — kiểm tra installed + chuyển cặp trước khi loop chạy.
    //

    [Fact]
    public async Task Start_WithOcrPair_ListsThenSetsPairBeforeLoopWork()
    {
        await using var peer = new Peer();
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult("HELLO") };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recA", true));
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), peer.Server, sources);

        var parameters = StartParams();
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA" };
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        // The queued frame is consumed by the loop's first-frame wait; a fresh frame
        // makes the loop do its first real work (an ocr.spot of its own).
        // Frame xếp hàng bị bước chờ frame đầu tiêu thụ; một frame mới khiến loop
        // làm việc thật đầu tiên (một ocr.spot của chính nó).
        await Task.Delay(400, TestContext.Current.CancellationToken);
        sources[^1].Raise(TinyFrame());

        // Wait until some ocr.spot happened (loop work or warmup — both come after the pair RPC).
        // Chờ tới khi có ocr.spot (loop hoặc warmup — cả hai đều sau RPC cặp model).
        await WaitUntilAsync(() => rpc.Calls.Any(c => c.Method == "ocr.spot"), "first ocr.spot");

        string[] methods = [.. rpc.Calls.Select(c => c.Method)];
        int listIx = Array.IndexOf(methods, "ocr.models.list");
        int setIx = Array.IndexOf(methods, "ocr.models.set");
        int spotIx = Array.IndexOf(methods, "ocr.spot");
        Assert.True(listIx >= 0, "ocr.models.list was not called");
        Assert.True(setIx >= 0, "ocr.models.set was not called");
        Assert.True(listIx < setIx, $"ocr.models.list must precede ocr.models.set (got {listIx} vs {setIx})");
        Assert.True(setIx < spotIx, $"ocr.models.set must precede the first ocr.spot (got {setIx} vs {spotIx})");
        Assert.Equal(1, methods.Count(m => m == "ocr.models.list"));
        Assert.Equal(1, methods.Count(m => m == "ocr.models.set"));

        // The pair switch must carry exactly the requested names.
        // Lời chuyển cặp phải mang đúng hai tên được yêu cầu.
        JsonNode? setParams = rpc.Calls.Single(c => c.Method == "ocr.models.set").Params;
        Assert.Equal("detA", setParams!["det"]!.GetValue<string>());
        Assert.Equal("recA", setParams!["rec"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_WithOcrPairNotInstalled_ThrowsOcrModelMissing()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recB", false));
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        var parameters = StartParams();
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recB" };
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("ocr-model-missing: detA/recB", ex.Message, StringComparison.Ordinal);

        // Failed before any side effect: no pair switch, no warmup spot, and the session
        // slot stays free — a start without ocr still works.
        // Thất bại trước mọi side effect: không chuyển cặp, không warmup spot, slot phiên
        // vẫn tự do — start không có ocr vẫn chạy được.
        Assert.DoesNotContain(rpc.Calls, c => c.Method == "ocr.models.set");
        Assert.DoesNotContain(rpc.Calls, c => c.Method == "ocr.spot");
        JsonNode? restarted = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(restarted!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_WithoutOcr_MakesNoModelCalls()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        // Backward compatible: the sidecar keeps its default pair — the host must not
        // even ask. Wait for real loop work (a fresh frame -> one ocr.spot) before
        // concluding, so the assertion sees a running pipeline.
        // Tương thích ngược: sidecar giữ cặp mặc định — host không hỏi gì cả. Chờ việc
        // thật của loop (frame mới -> một ocr.spot) rồi mới kết luận, để assert thấy
        // pipeline đang chạy.
        await Task.Delay(400, TestContext.Current.CancellationToken);
        sources[^1].Raise(TinyFrame());
        await WaitUntilAsync(() => rpc.Calls.Any(c => c.Method == "ocr.spot"), "first ocr.spot");
        Assert.DoesNotContain(rpc.Calls, c => c.Method.StartsWith("ocr.models.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_MalformedOcr_InvalidParams()
    {
        await using var host = CreateHost(
            new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var notAnObject = StartParams();
        notAnObject["ocr"] = "detA";
        var ex1 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(notAnObject, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex1.Code);
        Assert.Contains("ocr", ex1.Message, StringComparison.Ordinal);

        var emptyDet = StartParams();
        emptyDet["ocr"] = new JsonObject { ["det"] = "", ["rec"] = "recA" };
        var ex2 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(emptyDet, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex2.Code);

        var missingRec = StartParams();
        missingRec["ocr"] = new JsonObject { ["det"] = "detA" };
        var ex3 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(missingRec, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex3.Code);

        var badParams = StartParams();
        badParams["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["params"] = "nope" };
        var ex4 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(badParams, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex4.Code);
        Assert.Equal("'ocr.params' must be an object.", ex4.Message);
    }

    [Fact]
    public async Task Start_NoSidecar_WithOcrPair_InternalError()
    {
        await using var host = CreateHost(rpc: null, RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var parameters = StartParams();
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA" };
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("sidecar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    //
    // core.start ocr.mergeLines — parse-level checks only; the runtime effect (grouping) is
    // covered by Task 4/5.
    // core.start ocr.mergeLines — chỉ kiểm tra ở tầng parse; hiệu ứng runtime (gom dòng) do
    // Task 4/5 phủ.
    //

    [Fact]
    public async Task Start_MergeLinesTrue_ParsesOk()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recA", true));
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        var parameters = StartParams();
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["mergeLines"] = true };
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeLinesAbsent_DefaultsFalse()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeLinesNull_TreatedAsAbsent()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recA", true));
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        // JSON null = "keep the default (off)": no -32602.
        // JSON null = "giữ mặc định (tắt)": không -32602.
        var parameters = StartParams();
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["mergeLines"] = null };
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeLinesNonBool_InvalidParams()
    {
        await using var host = CreateHost(new FakeSidecarRpc(), RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var asString = StartParams();
        asString["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["mergeLines"] = "yes" };
        var ex1 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(asString, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex1.Code);
        Assert.Contains("mergeLines", ex1.Message, StringComparison.Ordinal);

        var asNumber = StartParams();
        asNumber["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["mergeLines"] = 1 };
        var ex2 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(asNumber, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex2.Code);
        Assert.Contains("mergeLines", ex2.Message, StringComparison.Ordinal);
    }

    //
    // core.start ocr.mergeGapRatio / mergeHeightRatio / mergeOverlapRatio — parse-level checks
    // only; the runtime effect (grouping with the custom caps) is covered by OcrPipelineTests.
    // core.start ocr.mergeGapRatio / mergeHeightRatio / mergeOverlapRatio — chỉ kiểm tra ở tầng
    // parse; hiệu ứng runtime (gom dòng theo ngưỡng tùy chọn) do OcrPipelineTests phủ.
    //
    // Dải ngưỡng do spec K2 ấn định (gap 0.1..2.0, height 1.0..3.0, overlap 0..1); test là
    // nơi duy nhất nhắc lại các con số đó.
    //

    /// <summary>
    /// start params with an <c>ocr</c> block whose pair is installed in the fake catalog, plus the
    /// given merge-threshold keys (a <see langword="null"/> <see cref="JsonNode"/> writes JSON null,
    /// omitting the pair leaves the key absent).
    /// Tham số start với khối <c>ocr</c> có cặp model đã cài trong catalog giả, cộng các key ngưỡng
    /// gộp được truyền (<see cref="JsonNode"/> <see langword="null"/> ghi JSON null, bỏ qua cặp
    /// thì key không có mặt).
    /// </summary>
    private static JsonObject StartParamsWithMerge(params (string Key, JsonNode? Value)[] merge)
    {
        var ocr = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["mergeLines"] = true };
        foreach ((string key, JsonNode? value) in merge)
            ocr[key] = value;
        var parameters = StartParams();
        parameters["ocr"] = ocr;
        return parameters;
    }

    /// <summary>
    /// Host whose fake sidecar reports detA/recA installed, so an <c>ocr</c> block reaches
    /// <c>ParseStart</c>'s validation instead of failing the model check first.
    /// Host có sidecar giả báo detA/recA đã cài, để khối <c>ocr</c> đi tới phần kiểm tra của
    /// <c>ParseStart</c> thay vì hỏng ở bước kiểm tra model trước.
    /// </summary>
    private static PipelineHost CreateHostWithInstalledPair(out FakeSidecarRpc rpc)
    {
        rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recA", true));
        return CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer());
    }

    [Fact]
    public async Task Start_MergeThresholdsInRange_ParsesOk()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        // In-range values (a mid-range gap, a wide height cap, no overlap floor): start must run.
        // Giá trị trong dải (gap giữa dải, cap chiều cao rộng, không có ngưỡng overlap): start
        // phải chạy được.
        JsonNode? started = await host.StartAsync(StartParamsWithMerge(
            ("mergeGapRatio", JsonValue.Create(0.25)),
            ("mergeHeightRatio", JsonValue.Create(2.0)),
            ("mergeOverlapRatio", JsonValue.Create(0.0))), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeThresholdsOnBounds_ParsesOk()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        // Spec K2 ranges are inclusive on both ends.
        // Dải theo spec K2 bao gồm cả hai biên.
        JsonNode? started = await host.StartAsync(StartParamsWithMerge(
            ("mergeGapRatio", JsonValue.Create(0.1)),
            ("mergeHeightRatio", JsonValue.Create(1.0)),
            ("mergeOverlapRatio", JsonValue.Create(1.0))), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeThresholdsNull_TreatedAsAbsent()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        // JSON null = "keep the shipped defaults (0.6 / 1.35 / 0.5)": no -32602.
        // JSON null = "giữ mặc định gốc (0.6 / 1.35 / 0.5)": không -32602.
        JsonNode? started = await host.StartAsync(StartParamsWithMerge(
            ("mergeGapRatio", null), ("mergeHeightRatio", null), ("mergeOverlapRatio", null)),
            CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeThresholdsAbsent_UsesCurrentDefaults()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        // Review Focus #1: an old settings.json has none of the three keys — absent keys fall
        // back to the shipped defaults, never 0 or 1.
        // Review Focus #1: settings.json cũ không có cả ba key — thiếu key phải rơi về mặc định
        // gốc, không bao giờ thành 0 hay 1.
        JsonNode? started = await host.StartAsync(StartParamsWithMerge(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeGapRatioNonNumber_InvalidParams()
    {
        await using var host = CreateHostWithInstalledPair(out FakeSidecarRpc rpc);

        // A JSON string number is not a number: silently coercing "0.25" would hide a typo in
        // a hand-edited settings.json.
        // Chuỗi JSON dạng số không phải số: âm thầm ép "0.25" sẽ giấu một lỗi gõ trong
        // settings.json sửa tay.
        var asString = StartParamsWithMerge(("mergeGapRatio", JsonValue.Create("0.25")));
        var ex1 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(asString, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex1.Code);
        Assert.Contains("ocr.mergeGapRatio", ex1.Message, StringComparison.Ordinal);
        Assert.Contains("0.1", ex1.Message, StringComparison.Ordinal);
        Assert.Contains("2.0", ex1.Message, StringComparison.Ordinal);

        var asBool = StartParamsWithMerge(("mergeGapRatio", JsonValue.Create(true)));
        var ex2 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(asBool, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex2.Code);
        Assert.Contains("ocr.mergeGapRatio", ex2.Message, StringComparison.Ordinal);

        // Review Focus #2: a rejected start leaves no half-started session — no sidecar call
        // at all, and the very next valid start still works.
        // Review Focus #2: start bị chặn không để lại phiên nửa vời — không gọi sidecar, và lần
        // start hợp lệ ngay sau vẫn chạy.
        Assert.DoesNotContain(rpc.Calls, c => c.Method.StartsWith("ocr.", StringComparison.Ordinal));
        JsonNode? restarted = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(restarted!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_MergeGapRatioOutOfRange_InvalidParams()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        var tooHigh = StartParamsWithMerge(("mergeGapRatio", JsonValue.Create(2.5)));
        var ex1 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(tooHigh, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex1.Code);
        Assert.Contains("ocr.mergeGapRatio", ex1.Message, StringComparison.Ordinal);

        var tooLow = StartParamsWithMerge(("mergeGapRatio", JsonValue.Create(0.05)));
        var ex2 = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(tooLow, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex2.Code);
        Assert.Contains("ocr.mergeGapRatio", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_MergeHeightRatioOutOfRange_InvalidParams()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        var parameters = StartParamsWithMerge(("mergeHeightRatio", JsonValue.Create(0.5)));
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("ocr.mergeHeightRatio", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1.0", ex.Message, StringComparison.Ordinal);
        Assert.Contains("3.0", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_MergeOverlapRatioOutOfRange_InvalidParams()
    {
        await using var host = CreateHostWithInstalledPair(out _);

        var parameters = StartParamsWithMerge(("mergeOverlapRatio", JsonValue.Create(1.5)));
        var ex = await Assert.ThrowsAsync<RpcMethodException>(
            () => host.StartAsync(parameters, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InvalidParams, ex.Code);
        Assert.Contains("ocr.mergeOverlapRatio", ex.Message, StringComparison.Ordinal);
    }

    //
    // Warmup — one fire-and-forget tiny spot after core.start; never blocks, never crashes.
    // Warmup — một spot nhỏ fire-and-forget sau core.start; không chặn, không crash.
    //

    [Fact]
    public async Task Start_FiresOneWarmupSpotAfterStart()
    {
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        // A source that never raises a frame: the loop waits for its first frame, so any
        // ocr.spot recorded here can only be the warmup.
        // Source không bao giờ phát frame: loop chờ frame đầu, nên ocr.spot ghi được
        // ở đây chỉ có thể là warmup.
        await using var host = CreateHost(
            rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(),
            sourceFactory: _ => new FakeCaptureSource());

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        await WaitUntilAsync(() => rpc.Calls.Any(c => c.Method == "ocr.spot"), "warmup ocr.spot");

        (string _, JsonNode? spotParams) = Assert.Single(rpc.Calls.Where(c => c.Method == "ocr.spot").ToArray());
        string? image = spotParams?["image"] is JsonValue v && v.TryGetValue<string>(out string? b64) ? b64 : null;
        Assert.False(string.IsNullOrEmpty(image), "warmup ocr.spot must carry a non-empty base64 image");
    }

    [Fact]
    public async Task Start_WarmupFailure_LoggedAndNonFatal()
    {
        var stderr = new StringWriter();
        var rpc = new FakeSidecarRpc();
        rpc.ThrowByMethod["ocr.spot"] = new InvalidOperationException("boom");
        await using var host = CreateHost(
            rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), stderr: stderr,
            sourceFactory: _ => new FakeCaptureSource());

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        // The failure is logged in the host's warning style and swallows cleanly.
        // Lỗi được ghi theo style cảnh báo của host và được nuốt sạch.
        await WaitUntilAsync(() => stderr.ToString().Contains("sidecar warmup failed"),
            "warmup failure warning");
        Assert.Contains("boom", stderr.ToString(), StringComparison.Ordinal);

        JsonNode? stopped = await host.StopAsync(null, CancellationToken.None);
        Assert.False(stopped!["running"]!.GetValue<bool>());
    }

    //
    // core.start ocr.params + ocr.config.set — schema-filtered engine params applied before
    // the model pair, and the forced deferReload while a session is running.
    // core.start ocr.params + ocr.config.set — tham số engine lọc theo schema, áp trước cặp
    // model, và deferReload bị ép khi phiên đang chạy.
    //

    [Fact]
    public async Task Start_OcrParamsNull_CountsAsAbsent()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.models.list"] = CatalogResult(("detA", true), ("recA", true));
        await using var host = CreateHost(
            rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        var parameters = StartParams();
        // JSON null = "keep the sidecar's current config": no -32602, no ocr.config.set.
        // JSON null = "giữ cấu hình hiện tại của sidecar": không -32602, không ocr.config.set.
        parameters["ocr"] = new JsonObject { ["det"] = "detA", ["rec"] = "recA", ["params"] = null };
        JsonNode? started = await host.StartAsync(parameters, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        Assert.DoesNotContain(rpc.Calls, c => c.Method == "ocr.config.set");
    }

    [Fact]
    public async Task Start_SendsFilteredOcrParams_BeforeModelsSet()
    {
        var sources = new List<FakeCaptureSource>();
        var stderr = new StringWriter();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        rpc.ResultsByMethod["ocr.config.schema"] = JsonNode.Parse("""
            {"params":[{"key":"device","group":"device","type":"string","default":null,"reloadRequired":true},
                       {"key":"text_det_thresh","group":"det","type":"float","default":0.3,"reloadRequired":false}]}
            """);
        rpc.ResultsByMethod["ocr.models.list"] = JsonNode.Parse("""
            {"catalog":[{"name":"PP-OCRv6_small_det","kind":"det","installed":true},
                        {"name":"PP-OCRv6_small_rec","kind":"rec","installed":true}]}
            """);
        await using var host = CreateHost(
            rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources, stderr);

        // One payload node reused by both starts: a second start on the same node must not
        // throw the JsonNode "node already has a parent" error.
        // Một node payload dùng cho cả hai lần start: lần start thứ hai trên cùng node không
        // được ném lỗi "node already has a parent" của JsonNode.
        JsonNode payload = JsonNode.Parse("""
            {"source":{"kind":"screen","monitor":0},
             "translate":{"provider":"google","src":"auto","dst":"vi"},
             "ocr":{"det":"PP-OCRv6_small_det","rec":"PP-OCRv6_small_rec",
                    "params":{"device":"cpu","bogus":1}}}
            """)!;
        JsonNode? started = await host.StartAsync(payload, CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        var setCall = rpc.Calls.Single(c => c.Method == "ocr.config.set");
        var sent = (JsonObject)setCall.Params!;
        Assert.True(sent["params"]!["device"]!.GetValue<string>() == "cpu");
        Assert.Null(sent["params"]!["bogus"]);           // key lạ bị lọc
        Assert.False(sent["deferReload"]!.GetValue<bool>());
        Assert.Contains("dropping unknown key 'bogus'", stderr.ToString(), StringComparison.Ordinal);
        // thứ tự: config.set trước models.set
        var methods = rpc.Calls.Select(c => c.Method).ToList();
        Assert.True(methods.IndexOf("ocr.config.set") < methods.IndexOf("ocr.models.set"));

        // gọi lần hai sau core.stop không được ném node-parent
        await host.StopAsync(null, CancellationToken.None);
        JsonNode? restarted = await host.StartAsync(payload, CancellationToken.None);
        Assert.True(restarted!["running"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SetOcrConfig_WhileRunning_ForcesDeferReloadTrue()
    {
        var sources = new List<FakeCaptureSource>();
        var rpc = new FakeSidecarRpc { NextResult = SpotResult() };
        await using var host = CreateHost(
            rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer(), sources);

        JsonNode? started = await host.StartAsync(StartParams(), CancellationToken.None);
        Assert.True(started!["running"]!.GetValue<bool>());

        await host.SetOcrConfigAsync(new JsonObject { ["params"] = new JsonObject { ["device"] = "cpu" } },
            CancellationToken.None);

        var sent = (JsonObject)rpc.Calls.Last(c => c.Method == "ocr.config.set").Params!;
        Assert.True(sent["deferReload"]!.GetValue<bool>());   // IsRunning => Core ép true
    }

    //
    // ocr.models.recommend proxy — forwards {src} to the sidecar and returns its {det, rec}
    // verbatim (the first-run wizard preselects the pair without parsing the catalog itself).
    // Proxy ocr.models.recommend — chuyển {src} sang sidecar và trả {det, rec} nguyên văn
    // (wizard lần chạy đầu chọn sẵn cặp model mà không phải tự đọc catalog).
    //

    [Fact]
    public async Task RecommendOcrModels_ForwardsSrcToSidecar_Verbatim()
    {
        var rpc = new FakeSidecarRpc();
        rpc.ResultsByMethod["ocr.models.recommend"] = new JsonObject { ["det"] = "d1", ["rec"] = "r1" };
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer());

        JsonNode? result = await host.RecommendOcrModelsAsync(new JsonObject { ["src"] = "ko" }, CancellationToken.None);

        Assert.Equal("d1", result!["det"]!.GetValue<string>());
        Assert.Equal("r1", result["rec"]!.GetValue<string>());

        // The sidecar must see exactly the method and params the wizard sent.
        // Sidecar phải thấy đúng method và params mà wizard gửi xuống.
        (string method, JsonNode? sent) = Assert.Single(rpc.Calls.Where(c => c.Method == "ocr.models.recommend").ToArray());
        Assert.Equal("ocr.models.recommend", method);
        Assert.Equal("ko", sent!["src"]!.GetValue<string>());
    }

    [Fact]
    public async Task RecommendOcrModels_NullSidecar_ThrowsMinus32603()
    {
        await using var host = CreateHost(rpc: null, RegistryWith(new CountingTranslator()), new JsonRpcServer());
        var ex = await Assert.ThrowsAsync<RpcMethodException>(async () =>
            await host.RecommendOcrModelsAsync(new JsonObject { ["src"] = "ko" }, CancellationToken.None));
        Assert.Equal(RpcErrorCodes.InternalError, ex.Code);
        Assert.Contains("sidecar", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecommendOcrModels_ParentedParams_ForwardsDetachedClone()
    {
        // Core's JSON-RPC server hands handlers a params node that is still a child of the
        // parsed request envelope (JsonRpcServer.DispatchRequestAsync). The proxy must not
        // hand that same node on: serializing the sidecar request re-parents it and throws
        // InvalidOperationException "The node already has a parent." — a -32603 on the wire.
        // Server JSON-RPC của Core đưa cho handler node params vẫn còn là con của envelope
        // đã parse (JsonRpcServer.DispatchRequestAsync). Proxy không được chuyển đúng node
        // đó: serialize request sidecar re-parent nó và ném InvalidOperationException
        // "The node already has a parent." — thành -32603 trên wire.
        var rpc = new FakeSidecarRpc();
        rpc.ResultsByMethod["ocr.models.recommend"] = new JsonObject { ["det"] = "d1", ["rec"] = "r1" };
        await using var host = CreateHost(rpc, RegistryWith(new CountingTranslator()), new JsonRpcServer());

        var envelope = new JsonObject { ["params"] = new JsonObject { ["src"] = "ko" } };
        JsonNode parented = envelope["params"]!;
        Assert.NotNull(parented.Parent); // precondition: the node is owned by the envelope

        JsonNode? result = await host.RecommendOcrModelsAsync(parented, CancellationToken.None);

        Assert.Equal("d1", result!["det"]!.GetValue<string>());
        (string _, JsonNode? sent) = Assert.Single(rpc.Calls.Where(c => c.Method == "ocr.models.recommend").ToArray());
        Assert.NotSame(parented, sent);
        Assert.Null(sent!.Parent);
        Assert.Equal("ko", sent["src"]!.GetValue<string>());
        Assert.Equal("""{"src":"ko"}""", parented.ToJsonString()); // caller's node untouched
    }
}
