using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.Camera;
using ReTran_App.Services.Preview;
using CoreServe = ReTran.Core.Serve;

public class PipelineControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _settingsPath;
    private readonly DbContextOptions<RetranDbContext> _options;
    private RetranDbContext? _controllerDb;

    public PipelineControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retran-pipeline-{Guid.NewGuid():N}.db");
        _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-pipeline-{Guid.NewGuid():N}.json");
        _options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        using var db = new RetranDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _controllerDb?.Dispose();
        foreach (string path in new[] { _dbPath, _settingsPath })
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Unique per-test preview section name so runs never touch the real channel. Tên vùng preview riêng cho mỗi test để không chạm channel thật.</summary>
    private static string UniquePreviewName(string stem) => $@"Local\ReTran.Preview.Test.{stem}.{Guid.NewGuid():N}";

    private PipelineController CreateController(
        FakeCorePeer peer,
        OverlayState overlay,
        PreviewState? preview = null,
        FakeCameraOutput? camera = null,
        string? previewMapName = null)
    {
        _controllerDb = new RetranDbContext(_options);
        return new PipelineController(
            peer,
            new AppSettingsService(_settingsPath),
            new TranslationCache(_controllerDb),
            overlay,
            NullLogger<PipelineController>.Instance,
            preview,
            camera,
            previewMapName ?? UniquePreviewName("controller"));
    }

    [Fact]
    public async Task Start_BuildsCoreStartParams_FromSettingsAndSelection()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(new AppSettings("google", "auto", "vi", null));
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 1, HwndHex: null),
            CancellationToken.None);

        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("core.start", call.Method);
        Assert.NotNull(call.Params);
        Assert.Null(peer.CallsAtStartCore); // core already running — no auto-start needed

        JsonObject source = call.Params!["source"]!.AsObject();
        Assert.Equal("screen", source["kind"]!.GetValue<string>());
        Assert.Equal(1, source["monitor"]!.GetValue<int>());

        JsonObject translate = call.Params!["translate"]!.AsObject();
        Assert.Equal("google", translate["provider"]!.GetValue<string>());
        Assert.Equal("auto", translate["src"]!.GetValue<string>());
        Assert.Equal("vi", translate["dst"]!.GetValue<string>());

        // Cặp model OCR mặc định (small) luôn đi kèm core.start.
        JsonObject ocr = call.Params!["ocr"]!.AsObject();
        Assert.Equal("PP-OCRv6_small_det", ocr["det"]!.GetValue<string>());
        Assert.Equal("PP-OCRv6_small_rec", ocr["rec"]!.GetValue<string>());

        Assert.Null(call.Params["ocrFps"]);
        Assert.Null(call.Params["captureFps"]);
    }

    [Fact]
    public async Task Start_SendsConfiguredOcrPair_FromSettings()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(new AppSettings("google", "auto", "vi", null,
            OcrDet: "PP-OCRv6_tiny_det", OcrRec: "PP-OCRv6_tiny_rec"));
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("core.start", call.Method);
        JsonObject ocr = call.Params!["ocr"]!.AsObject();
        Assert.Equal("PP-OCRv6_tiny_det", ocr["det"]!.GetValue<string>());
        Assert.Equal("PP-OCRv6_tiny_rec", ocr["rec"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_CoreNotRunning_AutoStartsFirst()
    {
        var peer = new FakeCorePeer { IsStarted = false };
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        Assert.True(peer.StartCoreCalled);
        Assert.True(peer.IsStarted);
        Assert.Equal(0, peer.CallsAtStartCore); // StartCoreAsync ran before the first CallAsync
        Assert.Equal("core.start", Assert.Single(peer.Calls).Method);
    }

    [Fact]
    public async Task FrameTranslated_Notification_PublishesOverlay()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("frame.translated", new JsonObject
        {
            ["seq"] = 1,
            ["w"] = 1920,
            ["h"] = 1080,
            ["boxes"] = new JsonArray(
                new JsonObject
                {
                    ["x"] = 0.10, ["y"] = 0.20, ["w"] = 0.30, ["h"] = 0.05,
                    ["text"] = "HELLO", ["translated"] = "Xin chào",
                },
                new JsonObject
                {
                    ["x"] = 0.50, ["y"] = 0.60, ["w"] = 0.20, ["h"] = 0.10,
                    ["text"] = "PENDING", ["translated"] = null,
                }),
        });

        OverlayFrame? frame = overlay.Latest;
        Assert.NotNull(frame);
        Assert.Equal(1920, frame!.FrameWidth);
        Assert.Equal(1080, frame.FrameHeight);
        Assert.Equal(2, frame.Boxes.Count);
        Assert.Equal("HELLO", frame.Boxes[0].Text);
        Assert.Equal("Xin chào", frame.Boxes[0].Translated);
        // translated:null keeps the source text so UI/OBS always have something to show.
        Assert.Equal("PENDING", frame.Boxes[1].Translated);
    }

    [Fact]
    public async Task FrameAnnotated_SlimNotification_PublishesPreviewAndCameraFromSharedMemory()
    {
        string mapName = UniquePreviewName("roundtrip");
        byte[] jpeg = [1, 2, 3, 4, 5];
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, jpeg);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, camera, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        // Core now notifies with {seq,w,h} only: the JPEG itself travels via the shared memory.
        // Core giờ chỉ notify {seq,w,h}: JPEG đi qua shared memory thay vì qua JSON.
        peer.RaiseNotification("frame.annotated", new JsonObject
        {
            ["seq"] = 42,
            ["w"] = 1280,
            ["h"] = 720,
        });

        PreviewFrame? frame = preview.Latest;
        Assert.NotNull(frame);
        Assert.Equal(42, frame!.Seq);
        Assert.Equal(1280, frame.W);
        Assert.Equal(720, frame.H);
        Assert.Equal(jpeg, Convert.FromBase64String(frame.JpegBase64));

        // The camera receives the very same bytes (not base64-decoded, the raw JPEG).
        // Camera nhận đúng những byte đó (không phải base64 giải mã, mà là JPEG thô).
        (long Seq, int W, int H, byte[] Jpeg) submit = Assert.Single(camera.Submits);
        Assert.Equal(42, submit.Seq);
        Assert.Equal(1280, submit.W);
        Assert.Equal(720, submit.H);
        Assert.Equal(jpeg, submit.Jpeg);
    }

    [Fact]
    public async Task FrameAnnotated_MissingPreviewSection_NoPublishNoThrow()
    {
        string mapName = UniquePreviewName("absent"); // no writer ever created this section

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, camera, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("frame.annotated", new JsonObject
        {
            ["seq"] = 1,
            ["w"] = 1280,
            ["h"] = 720,
        });

        Assert.Null(preview.Latest);
        Assert.Empty(camera.Submits);
    }

    [Fact]
    public async Task FrameAnnotated_StaleOrRepeatedNotificationSeq_Ignored()
    {
        string mapName = UniquePreviewName("stale");
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, [1, 2, 3]);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, camera, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 5, ["w"] = 1280, ["h"] = 720 });
        Assert.Single(camera.Submits);

        writer.Write(1280, 720, [9, 9, 9]); // MMF updated but the notification is a stale replay

        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 5, ["w"] = 1280, ["h"] = 720 });
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 4, ["w"] = 1280, ["h"] = 720 });

        // Neither the repeated seq nor the older seq may re-publish or re-submit.
        // Seq lặp lại lẫn seq cũ hơn không được publish hay submit lại.
        Assert.Single(camera.Submits);
        Assert.NotNull(preview.Latest);
        Assert.Equal([1, 2, 3], Convert.FromBase64String(preview.Latest!.JpegBase64));
    }

    [Fact]
    public async Task FrameAnnotated_SameMmfFrame_SecondNotificationSkipped()
    {
        string mapName = UniquePreviewName("sameframe");
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, [7, 7, 7]);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, camera, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        // Two notifications but only one MMF write happened: the second must not re-publish an
        // identical frame to preview/camera.
        // Hai thông báo nhưng chỉ một lần ghi MMF: lần hai không được re-publish khung giống hệt
        // cho preview/camera.
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 1, ["w"] = 1280, ["h"] = 720 });
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 2, ["w"] = 1280, ["h"] = 720 });

        Assert.Single(camera.Submits);
        Assert.Equal(1, preview.Latest!.Seq); // second notification skipped: latest stays seq 1
        Assert.Equal([7, 7, 7], Convert.FromBase64String(preview.Latest.JpegBase64));
    }

    [Fact]
    public async Task NewSession_RestartsSeqNotifications_AcceptedAgain()
    {
        string mapName = UniquePreviewName("restart");
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, [1, 1, 1]);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, camera, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 3, ["w"] = 1280, ["h"] = 720 });
        Assert.Single(camera.Submits);

        // A fresh session restarts the notification seq at 1 (Core semantics) and the wire-seq
        // guard is reset on start — but the shared-memory seqlock continues across writer
        // generations, so a STALE old-session notification arriving before the new writer's first
        // frame must NOT re-publish the old frame: its MMF seq is not above the one already read.
        // Phiên mới khởi động lại seq thông báo từ 1 (đặc tả Core) và guard seq trên wire được
        // reset lúc start — nhưng bộ đếm seqlock của vùng nhớ chạy liên tục giữa các thế hệ
        // writer, nên thông báo CŨ của phiên trước đến trước khung đầu của writer mới không được
        // publish lại: seq MMF của nó không cao hơn lần đọc đã xử lý.
        await controller.StopAsync(CancellationToken.None);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 1, ["w"] = 1280, ["h"] = 720 });
        Assert.Single(camera.Submits); // stale old-session frame replay — dropped

        // The new session's fresh frame (new MMF seq from the continuing seqlock) IS published again.
        // Khung mới của phiên mới (seq MMF mới từ seqlock chạy tiếp) vẫn được publish lại.
        writer.Write(1280, 720, [2, 2, 2]);
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 2, ["w"] = 1280, ["h"] = 720 });
        Assert.Equal(2, camera.Submits.Count);
        Assert.Equal([2, 2, 2], Convert.FromBase64String(preview.Latest!.JpegBase64));
    }

    [Fact]
    public async Task Stop_CallsCameraHold()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), camera: camera);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(1, camera.Holds);
    }

    [Fact]
    public async Task PipelineStateStopped_Notification_CallsCameraHold()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var camera = new FakeCameraOutput();
        PipelineController controller = CreateController(peer, new OverlayState(), camera: camera);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("pipeline.state", new JsonObject
        {
            ["running"] = false,
            ["reason"] = "sidecar-exited",
        });

        Assert.Equal(1, camera.Holds);
    }

    [Fact]
    public async Task Stop_WithoutCamera_DoesNotThrow()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        await controller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PipelineState_Notification_RaisesEvent()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        bool? running = null;
        string? reason = "not-raised";
        controller.PipelineStateChanged += (r, arg) => { running = r; reason = arg; };

        peer.RaiseNotification("pipeline.state", new JsonObject
        {
            ["running"] = false,
            ["reason"] = "sidecar-exited",
        });

        Assert.False(running);
        Assert.Equal("sidecar-exited", reason);
    }

    [Fact]
    public async Task Put_Notification_WritesCacheUpert()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("translate.put", new JsonObject
        {
            ["context"] = "screen:0",
            ["src"] = "auto",
            ["dst"] = "vi",
            ["entries"] = new JsonArray(
                new JsonObject { ["text"] = "HELLO", ["translated"] = "Xin chào" }),
        });

        // translate.put writes fire-and-forget — poll until the row lands in the temp DB.
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await _controllerDb!.Translations
                        .AnyAsync(e => e.Context == "screen:0" && e.Text == "HELLO", TestContext.Current.CancellationToken))
                {
                    break;
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // BUSY/LOCKED vs. the concurrent writer
            {
            }
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        TranslationEntry row = await _controllerDb!.Translations.SingleAsync(e => e.Text == "HELLO", TestContext.Current.CancellationToken);
        Assert.Equal("Xin chào", row.Translated);
        Assert.Equal("auto", row.Src);
        Assert.Equal("vi", row.Dst);
    }

    [Fact]
    public async Task RequestCacheHandler_HitsAndMisses_WireShape()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        var cache = new TranslationCache(_controllerDb!);
        await cache.PutAsync(
            "screen:0", "auto", "vi",
            [("HELLO", "Xin chào"), ("BYE", "Tạm biệt")],
            CancellationToken.None);

        Func<string, JsonNode?, Task<JsonNode?>> handler = peer.RequestHandler!;
        JsonNode? reply = await handler("translate.request_cache", new JsonObject
        {
            ["context"] = "screen:0",
            ["src"] = "auto",
            ["dst"] = "vi",
            ["texts"] = new JsonArray("HELLO", "CHƯA CÓ", "BYE", "CŨNG CHƯA"),
        });

        // Core parse đúng shape này: hits là object text→translated, misses là array theo thứ tự input.
        Assert.NotNull(reply);
        JsonObject hits = reply!["hits"]!.AsObject();
        Assert.Equal(2, hits.Count);
        Assert.Equal("Xin chào", hits["HELLO"]!.GetValue<string>());
        Assert.Equal("Tạm biệt", hits["BYE"]!.GetValue<string>());
        Assert.Null(hits["CHƯA CÓ"]); // miss không được lẫn vào hits

        JsonArray misses = reply["misses"]!.AsArray();
        Assert.Equal(2, misses.Count);
        Assert.Equal("CHƯA CÓ", misses[0]!.GetValue<string>());
        Assert.Equal("CŨNG CHƯA", misses[1]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestCacheHandler_EmptyOrUnknown_MissesAll()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        Func<string, JsonNode?, Task<JsonNode?>> handler = peer.RequestHandler!;

        JsonNode? empty = await handler("translate.request_cache", new JsonObject
        {
            ["context"] = "screen:0",
            ["src"] = "auto",
            ["dst"] = "vi",
            ["texts"] = new JsonArray(),
        });
        Assert.Empty(empty!["hits"]!.AsObject());
        Assert.Empty(empty!["misses"]!.AsArray());

        JsonNode? unknown = await handler("translate.request_cache", new JsonObject
        {
            ["context"] = "screen:0",
            ["src"] = "auto",
            ["dst"] = "vi",
            ["texts"] = new JsonArray("KHÔNG CÓ"),
        });
        Assert.Empty(unknown!["hits"]!.AsObject());
        Assert.Equal("KHÔNG CÓ", unknown!["misses"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public async Task CoreExited_RaisesPipelineStateChanged_WithCoreExitedReason()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);
        Assert.True(controller.IsRunning);

        bool? running = null;
        string? reason = "not-raised";
        controller.PipelineStateChanged += (r, arg) => { running = r; reason = arg; };

        peer.RaiseExited(0);

        Assert.False(running);
        Assert.Equal("core-exited", reason);
        Assert.False(controller.IsRunning); // controller chỉ relay signal, state thành stopped
    }

    [Fact]
    public async Task Start_SetsActiveSource_ToSelection()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        SourceSelection selection = new(UseWindow: true, Monitor: 0, HwndHex: "0x1234");

        await controller.StartAsync(selection, CancellationToken.None);

        Assert.Equal(selection, controller.ActiveSource);
    }

    [Fact]
    public void IsOcrModelMissing_MatchesOnlyTheOcrMissingRpcError()
    {
        // Lỗi -32603 "ocr-model-missing: <det>/<rec>" từ core.start → nhận diện được.
        Assert.True(PipelineController.IsOcrModelMissing(
            new RpcException(-32603, "ocr-model-missing: PP-OCRv6_small_det/PP-OCRv6_small_rec")));
        // Lỗi RPC khác và exception ngoài RpcException đều không khớp.
        Assert.False(PipelineController.IsOcrModelMissing(
            new RpcException(-32602, "unknown model: nope")));
        Assert.False(PipelineController.IsOcrModelMissing(
            new InvalidOperationException("ocr-model-missing: a/b")));
        Assert.False(PipelineController.IsOcrModelMissing(null));
    }

    [Fact]
    public async Task ListOcrModels_MapsCatalogEntries_FromWireShape()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "ocr.models.list"
            ? new JsonObject
            {
                ["catalog"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "PP-OCRv6_small_det", ["kind"] = "det",
                        ["langs"] = new JsonArray("en", "ja", "vi"),
                        ["sizeMb"] = 9.6, ["installed"] = true, ["inUse"] = true,
                    },
                    new JsonObject
                    {
                        ["name"] = "korean_PP-OCRv5_mobile_rec", ["kind"] = "rec",
                        ["langs"] = new JsonArray("ko", "en"),
                        ["sizeMb"] = 14.0, ["installed"] = false, ["inUse"] = false,
                    }),
                ["active"] = new JsonObject { ["det"] = "PP-OCRv6_small_det", ["rec"] = "PP-OCRv6_small_rec" },
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        IReadOnlyList<OcrModelInfo> models = await controller.ListOcrModelsAsync(CancellationToken.None);

        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("ocr.models.list", call.Method);
        Assert.Null(call.Params);

        Assert.Equal(2, models.Count);
        OcrModelInfo det = models[0];
        Assert.Equal("PP-OCRv6_small_det", det.Name);
        Assert.Equal("det", det.Kind);
        Assert.Equal(["en", "ja", "vi"], det.Langs);
        Assert.Equal(9.6, det.SizeMb);
        Assert.True(det.Installed);
        Assert.True(det.InUse);
        OcrModelInfo rec = models[1];
        Assert.Equal("korean_PP-OCRv5_mobile_rec", rec.Name);
        Assert.Equal("rec", rec.Kind);
        Assert.Equal(["ko", "en"], rec.Langs);
        Assert.Equal(14.0, rec.SizeMb);
        Assert.False(rec.Installed);
        Assert.False(rec.InUse);
    }

    [Fact]
    public async Task ListOcrModels_EmptyOrMalformedReply_YieldsEmptyList()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());

        IReadOnlyList<OcrModelInfo> models = await controller.ListOcrModelsAsync(CancellationToken.None);

        Assert.Empty(models); // FakeCorePeer mặc định trả {"running": false} — không có catalog.
    }

    [Fact]
    public async Task OcrModelActions_SendExpectedWireParams()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.DownloadOcrModelAsync("PP-OCRv6_tiny_rec", CancellationToken.None);
        await controller.DeleteOcrModelAsync("PP-OCRv6_tiny_rec", CancellationToken.None);
        await controller.SetOcrModelPairAsync("PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec", CancellationToken.None);

        Assert.Equal(3, peer.Calls.Count);
        Assert.Equal("ocr.models.download", peer.Calls[0].Method);
        Assert.Equal("PP-OCRv6_tiny_rec", peer.Calls[0].Params!["name"]!.GetValue<string>());
        Assert.Equal("ocr.models.delete", peer.Calls[1].Method);
        Assert.Equal("PP-OCRv6_tiny_rec", peer.Calls[1].Params!["name"]!.GetValue<string>());
        Assert.Equal("ocr.models.set", peer.Calls[2].Method);
        Assert.Equal("PP-OCRv6_tiny_det", peer.Calls[2].Params!["det"]!.GetValue<string>());
        Assert.Equal("PP-OCRv6_tiny_rec", peer.Calls[2].Params!["rec"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApplyOcrPairIfIdle_WhenIdle_SendsModelsSetAndReturnsTrue()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());

        bool applied = await controller.ApplyOcrPairIfIdleAsync(
            "PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec", CancellationToken.None);

        Assert.True(applied);
        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("ocr.models.set", call.Method);
        Assert.Equal("PP-OCRv6_tiny_det", call.Params!["det"]!.GetValue<string>());
        Assert.Equal("PP-OCRv6_tiny_rec", call.Params!["rec"]!.GetValue<string>());
    }

    [Fact]
    public async Task ApplyOcrPairIfIdle_WhenRunning_ReturnsFalseWithoutRpc()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        int callsBefore = peer.Calls.Count;

        // C8: đổi model giữa phiên bị hoãn — không RPC nào đi wire khi đang chạy.
        bool applied = await controller.ApplyOcrPairIfIdleAsync(
            "PP-OCRv6_tiny_det", "PP-OCRv6_tiny_rec", CancellationToken.None);

        Assert.False(applied);
        Assert.Equal(callsBefore, peer.Calls.Count); // no ocr.models.set went out mid-session
    }

    [Fact]
    public async Task Stop_ClearsActiveSource()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        Assert.NotNull(controller.ActiveSource);

        await controller.StopAsync(CancellationToken.None);

        Assert.Null(controller.ActiveSource);
    }

    [Fact]
    public async Task CoreExited_ClearsActiveSource()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        peer.RaiseExited(0);

        Assert.Null(controller.ActiveSource);
    }

    /// <summary>Records what the controller pushes to the virtual-camera output. Ghi lại những gì controller đẩy ra camera ảo.</summary>
    private sealed class FakeCameraOutput : ICameraOutput
    {
        public List<(long Seq, int W, int H, byte[] Jpeg)> Submits { get; } = [];
        public int Holds { get; private set; }

        public void Submit(long seq, int w, int h, byte[] jpeg) => Submits.Add((seq, w, h, jpeg));
        public void Hold() => Holds++;
        public void Dispose() { }
    }

    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public bool StartCoreCalled { get; private set; }
        public int? CallsAtStartCore { get; private set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public Func<string, JsonNode?, Task<JsonNode?>>? RequestHandler { get; private set; }

        /// <summary>Optional per-method canned reply; null (or a null return) falls back to the default shape. Phản hồi dựng sẵn theo method; null trả về mặc định.</summary>
        public Func<string, JsonNode?>? Reply { get; set; }

        public event Action<string, JsonNode?>? NotificationReceived;
        public event Action<Exception>? HandlerError;
        public event Action<int>? Exited;

        public Task StartCoreAsync(CancellationToken ct)
        {
            StartCoreCalled = true;
            CallsAtStartCore = Calls.Count;
            IsStarted = true;
            return Task.CompletedTask;
        }

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            Calls.Add((method, @params));
            JsonNode? result = (Reply?.Invoke(method))
                ?? (method == "core.start"
                    ? new JsonObject { ["running"] = true }
                    : new JsonObject { ["running"] = false });
            return Task.FromResult<JsonNode?>(result);
        }

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler)
            => RequestHandler = handler;

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);

        public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);
    }
}
