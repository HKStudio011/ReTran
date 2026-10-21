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

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            return Task.FromResult(NextResult);
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
}
