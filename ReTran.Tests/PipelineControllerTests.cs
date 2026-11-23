using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
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
        // Không có ghi đè engine → "params" vắng hẳn (Core giữ cấu hình sidecar hiện tại).
        Assert.Null(ocr["params"]);

        // Nhịp pipeline đi kèm mọi core.start; mặc định 3/10 khung mỗi giây.
        Assert.Equal(3, call.Params["ocrFps"]!.GetValue<int>());
        Assert.Equal(10, call.Params["captureFps"]!.GetValue<int>());
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
    public async Task Start_SendsMergeLinesFromSettings()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(new AppSettings("google", "auto", "vi", null));
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        // mergeLines luôn đi kèm core.start; mặc định true (Core cũ bỏ qua key lạ).
        JsonObject ocr = Assert.Single(peer.Calls).Params!["ocr"]!.AsObject();
        Assert.True(ocr["mergeLines"]!.GetValue<bool>());

        // OcrMergeLines = false → gửi false.
        await controller.StopAsync(CancellationToken.None);
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with { OcrMergeLines = false });
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        JsonObject ocrOff = peer.Calls.Last(call => call.Method == "core.start").Params!["ocr"]!.AsObject();
        Assert.False(ocrOff["mergeLines"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Start_SendsMergeThresholdsFromSettings()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with
        {
            OcrMergeGap = 0.2,
            OcrMergeHeight = 1.1,
            OcrMergeOverlap = 0.0,
        });
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        // Ba ngưỡng gộp luôn đi kèm core.start, nằm TRONG khối "ocr" đúng chỗ Core đọc
        // (PipelineHost.ParseStart → ParseRatio), và là số JSON — GetValue<double> ném nếu
        // node mang chuỗi.
        JsonObject ocr = Assert.Single(peer.Calls).Params!["ocr"]!.AsObject();
        Assert.Equal(0.2, ocr["mergeGapRatio"]!.GetValue<double>());
        Assert.Equal(1.1, ocr["mergeHeightRatio"]!.GetValue<double>());
        Assert.Equal(0.0, ocr["mergeOverlapRatio"]!.GetValue<double>());

        // AppSettings.Default ⇒ mặc định spec K2 0.6/1.35/0.5.
        await controller.StopAsync(CancellationToken.None);
        new AppSettingsService(_settingsPath).Save(AppSettings.Default);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        JsonObject ocrDefault = peer.Calls.Last(call => call.Method == "core.start").Params!["ocr"]!.AsObject();
        Assert.Equal(0.6, ocrDefault["mergeGapRatio"]!.GetValue<double>());
        Assert.Equal(1.35, ocrDefault["mergeHeightRatio"]!.GetValue<double>());
        Assert.Equal(0.5, ocrDefault["mergeOverlapRatio"]!.GetValue<double>());
    }

    [Fact]
    public async Task Start_ForwardsOutOfRangeMergeThresholds_Verbatim()
    {
        // Hợp đồng "App chuyển nguyên vẹn, Core là nơi duy nhất chặn dải": App không clamp,
        // không kiểm tra dải — giá trị ngoài dải (gap 2.5 > max 2.0) phải tới Core y hệt để
        // Core ném -32602 (test chặn dải ở PipelineHost thuộc Task 3).
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with { OcrMergeGap = 2.5 });
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        JsonObject ocr = Assert.Single(peer.Calls).Params!["ocr"]!.AsObject();
        Assert.Equal(2.5, ocr["mergeGapRatio"]!.GetValue<double>());
    }

    [Fact]
    public async Task StartAsync_SendsOcrParamsAndFps_AndSurvivesSecondStart()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(new AppSettings("google", "auto", "vi", null,
            OcrEngine: new Dictionary<string, JsonNode?> { ["text_det_thresh"] = 0.35 },
            OcrFps: 5, CaptureFps: 12));
        PipelineController controller = CreateController(peer, new OverlayState());
        var source = new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null);

        await controller.StartAsync(source, CancellationToken.None);

        JsonObject sent = Assert.Single(peer.Calls).Params!.AsObject();
        Assert.Equal(0.35, sent["ocr"]!["params"]!["text_det_thresh"]!.GetValue<double>());
        Assert.Equal(5, sent["ocrFps"]!.GetValue<int>());
        Assert.Equal(12, sent["captureFps"]!.GetValue<int>());

        await controller.StopAsync(CancellationToken.None);
        // Kiểm tra tích hợp start → stop → start: mỗi lần Load() đọc lại file nên node là node
        // mới — chuỗi lệnh phải đi trọn và map ghi đè vẫn gửi nguyên giá trị.
        // Bảo đảm "cùng node dùng lại không ném node-parent" nằm ở test
        // BuildOcrParams_TwiceWithSameDictionary_ClonesAndKeepsCallerNodesUnparented bên dưới.
        await controller.StartAsync(source, CancellationToken.None);
        Assert.Equal(2, peer.Calls.Count(call => call.Method == "core.start"));
        // "Calls[1]" là core.stop; lần start thứ hai là call cuối cùng mang method core.start.
        JsonObject resent = peer.Calls.Last(call => call.Method == "core.start").Params!.AsObject();
        Assert.Equal(0.35, resent["ocr"]!["params"]!["text_det_thresh"]!.GetValue<double>());
    }

    [Fact]
    public void BuildOcrParams_TwiceWithSameDictionary_ClonesAndKeepsCallerNodesUnparented()
    {
        var overrides = new Dictionary<string, JsonNode?>
        {
            ["text_det_thresh"] = 0.35,
            ["cpu_threads"] = 4,
        };

        JsonObject? first = PipelineController.BuildOcrParams(overrides);
        // Lần hai với CÙNG dictionary: nếu BuildOcrParams gắn thẳng node của caller vào params,
        // lần hai sẽ ném "The node already has a parent" — đây là bảo đảm node-parent của core.start.
        JsonObject? second = PipelineController.BuildOcrParams(overrides);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(0.35, first!["text_det_thresh"]!.GetValue<double>());
        Assert.Equal(4, first["cpu_threads"]!.GetValue<int>());
        Assert.Equal(0.35, second!["text_det_thresh"]!.GetValue<double>());
        Assert.Equal(4, second["cpu_threads"]!.GetValue<int>());
        // Node của caller chưa từng bị đổi parent → settings dùng lại được cho lần start sau.
        Assert.Null(overrides["text_det_thresh"]!.Parent);
        Assert.Null(overrides["cpu_threads"]!.Parent);
        Assert.NotSame(overrides["text_det_thresh"], first["text_det_thresh"]);
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
    public async Task HandleFrameTranslated_ParagraphBox_FlagPropagates()
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
                    ["x"] = 0.10, ["y"] = 0.20, ["w"] = 0.30, ["h"] = 0.10,
                    ["text"] = "LINE ONE", ["translated"] = "Dòng một",
                    ["paragraph"] = true,
                },
                new JsonObject
                {
                    ["x"] = 0.50, ["y"] = 0.60, ["w"] = 0.20, ["h"] = 0.10,
                    ["text"] = "SINGLE", ["translated"] = "Đơn",
                }),
        });

        OverlayFrame? frame = overlay.Latest;
        Assert.NotNull(frame);
        // Khối gộp nhiều dòng (Task 4) mang "paragraph":true → IsParagraph truyền xuống overlay.
        Assert.True(frame!.Boxes[0].IsParagraph);
        // Box không có key "paragraph" → false (Core cũ / dòng đơn).
        Assert.False(frame.Boxes[1].IsParagraph);
    }

    [Fact]
    public async Task FrameAnnotated_SlimNotification_PublishesPreviewFromSharedMemory()
    {
        string mapName = UniquePreviewName("roundtrip");
        byte[] jpeg = [1, 2, 3, 4, 5];
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, jpeg);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, mapName);
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
    }

    [Fact]
    public async Task FrameAnnotated_MissingPreviewSection_NoPublishNoThrow()
    {
        string mapName = UniquePreviewName("absent"); // no writer ever created this section

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, mapName);
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
    }

    [Fact]
    public async Task FrameAnnotated_StaleOrRepeatedNotificationSeq_Ignored()
    {
        string mapName = UniquePreviewName("stale");
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, [1, 2, 3]);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 5, ["w"] = 1280, ["h"] = 720 });
        Assert.NotNull(preview.Latest);
        Assert.Equal(5, preview.Latest!.Seq);

        writer.Write(1280, 720, [9, 9, 9]); // MMF updated but the notification is a stale replay

        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 5, ["w"] = 1280, ["h"] = 720 });
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 4, ["w"] = 1280, ["h"] = 720 });

        // Neither the repeated seq nor the older seq may re-publish.
        // Seq lặp lại lẫn seq cũ hơn không được publish lại.
        Assert.Equal(5, preview.Latest!.Seq);
        Assert.Equal([1, 2, 3], Convert.FromBase64String(preview.Latest.JpegBase64));
    }

    [Fact]
    public async Task FrameAnnotated_SameMmfFrame_SecondNotificationSkipped()
    {
        string mapName = UniquePreviewName("sameframe");
        using var writer = new CoreServe.PreviewChannelWriter(mapName);
        writer.Write(1280, 720, [7, 7, 7]);

        var peer = new FakeCorePeer { IsStarted = true };
        var preview = new PreviewState();
        PipelineController controller = CreateController(peer, new OverlayState(), preview, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);

        // Two notifications but only one MMF write happened: the second must not re-publish an
        // identical frame to the preview.
        // Hai thông báo nhưng chỉ một lần ghi MMF: lần hai không được re-publish khung giống hệt
        // cho preview.
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 1, ["w"] = 1280, ["h"] = 720 });
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 2, ["w"] = 1280, ["h"] = 720 });

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
        PipelineController controller = CreateController(peer, new OverlayState(), preview, mapName);
        await controller.StartAsync(
            new SourceSelection(UseWindow: false, Monitor: 0, HwndHex: null),
            CancellationToken.None);
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 3, ["w"] = 1280, ["h"] = 720 });
        Assert.Equal(3, preview.Latest!.Seq);

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
        Assert.Equal(3, preview.Latest!.Seq); // stale old-session frame replay — dropped

        // The new session's fresh frame (new MMF seq from the continuing seqlock) IS published again.
        // Khung mới của phiên mới (seq MMF mới từ seqlock chạy tiếp) vẫn được publish lại.
        writer.Write(1280, 720, [2, 2, 2]);
        peer.RaiseNotification("frame.annotated", new JsonObject { ["seq"] = 2, ["w"] = 1280, ["h"] = 720 });
        Assert.Equal(2, preview.Latest!.Seq);
        Assert.Equal([2, 2, 2], Convert.FromBase64String(preview.Latest.JpegBase64));
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
                        ["downloading"] = true,
                        ["progress"] = new JsonObject
                        {
                            ["receivedBytes"] = 100L, ["totalBytes"] = 400L, ["bytesPerSec"] = 50.0,
                        },
                    },
                    new JsonObject
                    {
                        ["name"] = "arabic_PP-OCRv5_mobile_rec", ["kind"] = "rec",
                        ["langs"] = new JsonArray("ar", "en"),
                        ["sizeMb"] = 14.0, ["installed"] = false, ["inUse"] = false,
                        ["downloading"] = false, ["downloadError"] = "download boom",
                    }),
                ["active"] = new JsonObject { ["det"] = "PP-OCRv6_small_det", ["rec"] = "PP-OCRv6_small_rec" },
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        IReadOnlyList<OcrModelInfo> models = await controller.ListOcrModelsAsync(CancellationToken.None);

        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("ocr.models.list", call.Method);
        Assert.Null(call.Params);

        Assert.Equal(3, models.Count);
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
        Assert.True(rec.Downloading);
        Assert.NotNull(rec.Progress);
        Assert.Equal(100, rec.Progress!.ReceivedBytes);
        Assert.Equal(400, rec.Progress.TotalBytes);
        Assert.Equal(50.0, rec.Progress.BytesPerSec);
        Assert.Null(rec.DownloadError);
        OcrModelInfo failed = models[2];
        Assert.False(failed.Downloading);
        Assert.Equal("download boom", failed.DownloadError);
        Assert.Null(failed.Progress);
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
    public async Task Start_LocalLlmm_PrewarmsBeforeCoreStart()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        PipelineController controller = CreateController(peer, new OverlayState());
        int startupChanges = 0;
        controller.LlmStartupChanged += () => startupChanges++;
        var source = new SourceSelection(false, 0, null);
        IPipelineControl floatingMenuSurface = controller;

        await floatingMenuSurface.StartAsync(source, CancellationToken.None);

        Assert.Equal(
            ["llm.models.set", "llm.server.start", "llm.server.status", "core.start"],
            peer.Calls.Select(call => call.Method));
        Assert.True(controller.IsRunning);
        Assert.Equal(source, controller.ActiveSource);
        Assert.True(startupChanges > 0); // the shared controller publishes even through IPipelineControl
        Assert.Null(controller.LlmStartup); // successful core.start closes the popup
    }

    [Fact]
    public async Task Start_CancelBeforeCoreStart_StopsServerWithoutRunning()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Called += method =>
        {
            if (method == "llm.server.start") accepted.TrySetResult();
        };
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(state: "loading", phase: "loading"),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        Task start = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await controller.CancelLlmStartupAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.DoesNotContain(peer.Calls, call => call.Method == "core.start");
        Assert.Contains(peer.Calls, call => call.Method == "llm.server.stop");
        Assert.False(controller.IsRunning);
        Assert.Null(controller.ActiveSource);
        Assert.Null(controller.LlmStartup);
    }

    [Fact]
    public async Task Start_CancelWhenReadyStatusArrives_DoesNotSendCoreStart()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        PipelineController? controller = null;
        Task? cancel = null;
        peer.AsyncReply = (method, _, _) => method switch
        {
            "llm.server.start" => Task.FromResult<JsonNode?>(AcceptedLocalStart()),
            "llm.server.status" => CancelAndReturnReady(),
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = false }),
        };
        controller = CreateController(peer, new OverlayState());

        Task start = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.NotNull(cancel);
        await cancel;

        Assert.DoesNotContain(peer.Calls, call => call.Method == "core.start");
        Assert.Contains(peer.Calls, call => call.Method == "llm.server.stop");
        Assert.False(controller.IsRunning);

        Task<JsonNode?> CancelAndReturnReady()
        {
            cancel = controller!.CancelLlmStartupAsync(CancellationToken.None);
            return Task.FromResult<JsonNode?>(LocalStatus());
        }
    }

    [Fact]
    public async Task Start_CancelAfterCoreStart_RollsBackWithCoreStop()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        var coreStartSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCoreStart = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AsyncReply = (method, _, _) => method switch
        {
            "core.start" => AwaitCoreStart(),
            "llm.server.start" => Task.FromResult<JsonNode?>(AcceptedLocalStart()),
            "llm.server.status" => Task.FromResult<JsonNode?>(LocalStatus()),
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = false }),
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        Task start = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        await coreStartSent.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Task cancel = controller.CancelLlmStartupAsync(CancellationToken.None);
        Assert.False(cancel.IsCompleted);
        Assert.DoesNotContain(peer.Calls, call => call.Method == "core.stop");

        finishCoreStart.SetResult(new JsonObject { ["running"] = true });
        await cancel.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);

        Assert.Contains(peer.Calls, call => call.Method == "core.stop");
        Assert.False(controller.IsRunning);
        Assert.Null(controller.ActiveSource);
        Assert.Null(controller.LlmStartup);

        Task<JsonNode?> AwaitCoreStart()
        {
            coreStartSent.TrySetResult();
            return finishCoreStart.Task;
        }
    }

    [Fact]
    public async Task Start_CoreStartFailure_StopsLlmAndDoesNotRun()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(),
            "core.start" => throw new RpcException(-32603, "core start exploded"),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        RpcException error = await Assert.ThrowsAsync<RpcException>(() =>
            controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None));

        Assert.Equal("core start exploded", error.Message);
        Assert.Contains(peer.Calls, call => call.Method == "core.stop");
        Assert.Contains(peer.Calls, call => call.Method == "llm.server.stop");
        Assert.False(controller.IsRunning);
        Assert.Null(controller.ActiveSource);
        Assert.Equal("error", controller.LlmStartup?.Phase);
        Assert.Equal("core start exploded", controller.LlmStartup?.Error);
    }

    [Fact]
    public async Task Start_SecondCallerRejected_AndSourceUnchanged()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Called += method =>
        {
            if (method == "llm.server.start") accepted.TrySetResult();
        };
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(state: "loading", phase: "loading"),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());
        var firstSource = new SourceSelection(false, 0, null);
        Task first = controller.StartAsync(firstSource, CancellationToken.None);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(new SourceSelection(true, 0, "0xBEEF"), CancellationToken.None));

        Assert.Equal("pipeline start is already in progress", error.Message);
        Assert.Null(controller.ActiveSource);
        Assert.Single(peer.Calls, call => call.Method == "llm.server.start");
        await controller.CancelLlmStartupAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("api")]
    public async Task Start_GoogleOrApi_DoesNotPrewarm(string mode)
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with
        {
            Provider = mode == "google" ? "google" : "llm",
            LlmMode = mode == "api" ? "api" : "local",
        });
        PipelineController controller = CreateController(peer, new OverlayState());
        int startupChanges = 0;
        controller.LlmStartupChanged += () => startupChanges++;

        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        Assert.Equal("core.start", Assert.Single(peer.Calls).Method);
        Assert.Equal(0, startupChanges);
        Assert.Null(controller.LlmStartup);
        Assert.True(controller.IsRunning);
    }

    [Fact]
    public async Task Start_PublishesImmediately_AndCancelsBeforeAccepted()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        var serverStartCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AsyncReply = (method, _, ct) => method switch
        {
            "llm.server.start" => WaitForAcceptance(method, ct),
            "llm.server.status" => Task.FromResult<JsonNode?>(LocalStatus(state: "loading", phase: "loading")),
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = false }),
        };
        PipelineController controller = CreateController(peer, new OverlayState());
        Task start = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        try
        {
            await serverStartCalled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.NotNull(controller.LlmStartup); // local generation is visible before Core accepts an attempt
            Assert.Equal(0, controller.LlmStartup.Attempt);

            await controller.CancelLlmStartupAsync(CancellationToken.None);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            Assert.DoesNotContain(peer.Calls, call => call.Method == "core.start");
            Assert.Single(peer.Calls, call => call.Method == "llm.server.stop");
            Assert.Null(controller.LlmStartup);
        }
        finally
        {
            if (!start.IsCompleted)
            {
                try { await controller.CancelLlmStartupAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            }
            try { await start; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
        }

        async Task<JsonNode?> WaitForAcceptance(string method, CancellationToken ct)
        {
            serverStartCalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return AcceptedLocalStart();
        }
    }

    [Fact]
    public async Task Cancel_StalePreAcceptGeneration_DoesNotStopAnotherSession()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        int serverStarts = 0;
        var firstCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.AsyncReply = (method, _, ct) => method switch
        {
            "llm.server.start" => HoldServerStart(ct),
            _ => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = false }),
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        Task first = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        await firstCalled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        long staleGeneration = controller.LlmStartup!.Generation;
        await controller.CancelLlmStartupAsync(staleGeneration, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Task second = controller.StartAsync(new SourceSelection(false, 1, null), CancellationToken.None);
        await secondCalled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        int stopsBeforeStaleCancel = peer.Calls.Count(call => call.Method == "llm.server.stop");
        await controller.CancelLlmStartupAsync(staleGeneration, CancellationToken.None);

        Assert.False(second.IsCompleted);
        Assert.Equal(stopsBeforeStaleCancel, peer.Calls.Count(call => call.Method == "llm.server.stop"));
        Assert.NotEqual(staleGeneration, controller.LlmStartup!.Generation);

        await controller.CancelLlmStartupAsync(controller.LlmStartup.Generation, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        async Task<JsonNode?> HoldServerStart(CancellationToken ct)
        {
            int current = Interlocked.Increment(ref serverStarts);
            if (current == 1) firstCalled.TrySetResult();
            if (current == 2) secondCalled.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return AcceptedLocalStart();
        }
    }

    [Theory]
    [InlineData("google")]
    [InlineData("api")]
    [InlineData("local")]
    public async Task Start_AfterSuccessfulStart_RejectsWithoutStoppingExistingSession(string mode)
    {
        var peer = new FakeCorePeer { IsStarted = true };
        if (mode == "local") UseLocalLlm();
        else
        {
            new AppSettingsService(_settingsPath).Save(AppSettings.Default with
            {
                Provider = mode == "google" ? "google" : "llm",
                LlmMode = mode == "api" ? "api" : "local",
            });
        }
        PipelineController controller = CreateController(peer, new OverlayState());
        var source = new SourceSelection(false, 0, null);
        await controller.StartAsync(source, CancellationToken.None);
        int callsAfterSuccess = peer.Calls.Count;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(new SourceSelection(true, 0, "0xBEEF"), CancellationToken.None));

        Assert.Equal("pipeline already running", error.Message);
        Assert.Equal(callsAfterSuccess, peer.Calls.Count);
        Assert.DoesNotContain(peer.Calls, call => call.Method is "core.stop" or "llm.server.stop");
        Assert.True(controller.IsRunning);
        Assert.Equal(source, controller.ActiveSource);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("local")]
    public async Task Start_CoreAlreadyRunning_NeverRollsBackExistingSession(string mode)
    {
        var peer = new FakeCorePeer { IsStarted = true };
        if (mode == "local") UseLocalLlm();
        else new AppSettingsService(_settingsPath).Save(AppSettings.Default);
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(),
            "core.start" => throw new RpcException(-32600, "pipeline already running (core.stop first)."),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        RpcException error = await Assert.ThrowsAsync<RpcException>(() =>
            controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None));

        Assert.Contains("pipeline already running", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Calls, call => call.Method is "core.stop" or "llm.server.stop");
        Assert.False(controller.IsRunning); // this controller never owned the external Core session
        Assert.Null(controller.ActiveSource);
    }

    [Fact]
    public async Task Cancel_LlmStopFailure_IsNotReportedAsCleanCancellation()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Called += method => { if (method == "llm.server.start") accepted.TrySetResult(); };
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(state: "loading", phase: "loading"),
            "llm.server.stop" => throw new RpcException(-32603, "stop was not acknowledged"),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());
        Task start = controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        InvalidOperationException cancelError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.CancelLlmStartupAsync(CancellationToken.None));
        InvalidOperationException startError = await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        Assert.Contains("retry", cancelError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("llm.server.stop", startError.Message, StringComparison.Ordinal);
        Assert.Equal("error", controller.LlmStartup?.Phase);
        Assert.Contains("retry", controller.LlmStartup?.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(controller.IsRunning);
        Assert.Null(controller.ActiveSource);
    }

    [Fact]
    public async Task Start_RollbackFailure_SurfacesCleanupErrorAndRetryGuidance()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" => LocalStatus(),
            "core.start" => throw new RpcException(-32603, "core start exploded"),
            "llm.server.stop" => throw new RpcException(-32603, "stop was not acknowledged"),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None));

        Assert.Contains("core start exploded", error.Message, StringComparison.Ordinal);
        Assert.Contains("llm.server.stop", error.Message, StringComparison.Ordinal);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("error", controller.LlmStartup?.Phase);
        Assert.Contains("retry", controller.LlmStartup?.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("superseded")]
    public async Task Start_LocalLlmm_StatusErrorOrSuperseded_StopsBeforeCoreStart(string failure)
    {
        var peer = new FakeCorePeer { IsStarted = true };
        UseLocalLlm();
        peer.Reply = method => method switch
        {
            "llm.server.start" => AcceptedLocalStart(),
            "llm.server.status" when failure == "error" => new JsonObject
            {
                ["state"] = "error", ["model"] = "Hy-MT2-1.8B-UD-Q4_K_XL",
                ["error"] = "load exploded", ["attempt"] = 1L, ["phase"] = "error",
                ["bytesLoaded"] = null, ["bytesTotal"] = null, ["bytesPerSec"] = null,
                ["progressAvailable"] = false,
            },
            "llm.server.status" => LocalStatus(attempt: 2),
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None));

        Assert.Contains(failure == "error" ? "load exploded" : "superseded", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(peer.Calls, call => call.Method == "core.start");
        Assert.Contains(peer.Calls, call => call.Method == "llm.server.stop");
        Assert.False(controller.IsRunning);
    }

    private void UseLocalLlm() => new AppSettingsService(_settingsPath).Save(AppSettings.Default with
    {
        Provider = "llm",
        LlmMode = "local",
        LlmActiveModel = "Hy-MT2-1.8B-UD-Q4_K_XL",
    });

    private static JsonObject AcceptedLocalStart() => new()
    {
        ["accepted"] = true,
        ["attempt"] = 1L,
        ["model"] = "Hy-MT2-1.8B-UD-Q4_K_XL",
    };

    private static JsonObject LocalStatus(long attempt = 1, string state = "ready", string phase = "ready") => new()
    {
        ["state"] = state,
        ["model"] = "Hy-MT2-1.8B-UD-Q4_K_XL",
        ["error"] = null,
        ["attempt"] = attempt,
        ["phase"] = phase,
        ["bytesLoaded"] = 8L,
        ["bytesTotal"] = 8L,
        ["bytesPerSec"] = 1024.0,
        ["progressAvailable"] = true,
    };

    [Fact]
    public async Task Start_LlmLocal_SendsModeAndActiveModelOnly()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with
        {
            Provider = "llm",
            LlmMode = "local",
            LlmActiveModel = "Hy-MT2-1.8B-UD-Q4_K_XL",
        });
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        // Local: đồng bộ con trỏ active của catalog TRƯỚC core.start để InUse khớp model phiên;
        // prompt null không đi wire và field API không lẫn vào (spec D1/D10).
        Assert.Equal(4, peer.Calls.Count);
        Assert.Equal("llm.models.set", peer.Calls[0].Method);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", peer.Calls[0].Params!["name"]!.GetValue<string>());
        Assert.Equal("llm.server.start", peer.Calls[1].Method);
        Assert.Equal("llm.server.status", peer.Calls[2].Method);
        Assert.Equal("core.start", peer.Calls[3].Method);
        JsonObject llm = peer.Calls[3].Params!["translate"]!["llm"]!.AsObject();
        Assert.Equal("local", llm["mode"]!.GetValue<string>());
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", llm["model"]!.GetValue<string>());
        Assert.Null(llm["baseUrl"]);
        Assert.Null(llm["apiKey"]);
        Assert.Null(llm["systemPrompt"]);
        Assert.Null(llm["userPrompt"]);
    }

    [Fact]
    public async Task Start_LlmLocal_MissingActiveModel_KeepsRecoveryError()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "llm.models.set"
            ? throw new RpcException(-32602, "model is not installed: Hy-MT2-1.8B-UD-Q4_K_XL")
            : null;
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with
        {
            Provider = "llm",
            LlmMode = "local",
            LlmActiveModel = "Hy-MT2-1.8B-UD-Q4_K_XL",
        });
        PipelineController controller = CreateController(peer, new OverlayState());

        RpcException ex = await Assert.ThrowsAsync<RpcException>(() =>
            controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None));

        Assert.Equal(-32603, ex.Code);
        Assert.Equal("llm-model-missing: Hy-MT2-1.8B-UD-Q4_K_XL", ex.Message);
        Assert.True(PipelineController.IsLlmComponentMissing(ex));
        Assert.Single(peer.Calls); // core.start is not sent after the failed preflight
        Assert.Equal("llm.models.set", peer.Calls[0].Method);
    }

    [Fact]
    public async Task Start_LlmApi_SendsEndpointFieldsAndPromptOverrides()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default with
        {
            Provider = "llm",
            LlmMode = "api",
            LlmBaseUrl = "http://localhost:1234/v1",
            LlmApiKey = "sk-test",
            LlmApiModel = "gpt-4o-mini",
            LlmSystemPrompt = "You translate game dialogue.",
            LlmUserPrompt = "Translate into {target_lang}: {source_text}",
        });
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        JsonObject llm = Assert.Single(peer.Calls).Params!["translate"]!["llm"]!.AsObject();
        Assert.Equal("api", llm["mode"]!.GetValue<string>());
        Assert.Equal("http://localhost:1234/v1", llm["baseUrl"]!.GetValue<string>());
        Assert.Equal("sk-test", llm["apiKey"]!.GetValue<string>());
        Assert.Equal("gpt-4o-mini", llm["model"]!.GetValue<string>());
        Assert.Equal("You translate game dialogue.", llm["systemPrompt"]!.GetValue<string>());
        Assert.Equal("Translate into {target_lang}: {source_text}", llm["userPrompt"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_Google_OmitsLlmBlock()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default); // provider google
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        // Provider khác llm → translate không có khối llm (Core của bản cũ cũng bỏ qua nếu có).
        JsonObject translate = Assert.Single(peer.Calls).Params!["translate"]!.AsObject();
        Assert.Null(translate["llm"]);
    }

    [Fact]
    public void IsLlmComponentMissing_MatchesOnlyTheLlmMissingRpcErrors()
    {
        // Hai code -32603 của Core factory cho local mode → nhận diện được.
        Assert.True(PipelineController.IsLlmComponentMissing(
            new RpcException(-32603, "llm-model-missing: Hy-MT2-1.8B-UD-Q4_K_XL")));
        Assert.True(PipelineController.IsLlmComponentMissing(
            new RpcException(-32603, "llm-runtime-missing: download llama.cpp from Setup or Settings.")));
        // Lỗi khác và exception ngoài RpcException đều không khớp.
        Assert.False(PipelineController.IsLlmComponentMissing(
            new RpcException(-32602, "missing 'translate.llm' object")));
        Assert.False(PipelineController.IsLlmComponentMissing(
            new InvalidOperationException("llm-model-missing: x")));
        Assert.False(PipelineController.IsLlmComponentMissing(null));
    }

    [Fact]
    public async Task LlmModelsList_MapsArrayWireShape()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "llm.models.list"
            ? new JsonArray(
                new JsonObject
                {
                    ["name"] = "Hy-MT2-1.8B-UD-Q4_K_XL", ["quant"] = "UD-Q4_K_XL",
                    ["sizeClass"] = "1.8B", ["sizeBytes"] = 1170000000L,
                    ["installed"] = true, ["inUse"] = true, ["downloading"] = false,
                },
                new JsonObject
                {
                    ["name"] = "Hy-MT2-7B-Q4_K_M", ["quant"] = "Q4_K_M",
                    ["sizeClass"] = "7B", ["sizeBytes"] = 4780000000L,
                    ["installed"] = false, ["inUse"] = false, ["downloading"] = false,
                    ["downloadError"] = "disk full",
                })
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        IReadOnlyList<LlmModelInfo> models = await controller.ListLlmModelsAsync(CancellationToken.None);

        // Core trả một ARRAY trực tiếp (không bọc object) — App đọc đúng shape camelCase này.
        Assert.Equal("llm.models.list", Assert.Single(peer.Calls).Method);
        Assert.Equal(2, models.Count);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", models[0].Name);
        Assert.Equal("UD-Q4_K_XL", models[0].Quant);
        Assert.Equal("1.8B", models[0].SizeClass);
        Assert.Equal(1170000000L, models[0].SizeBytes);
        Assert.True(models[0].Installed);
        Assert.True(models[0].InUse);
        Assert.False(models[0].Downloading);
        Assert.Equal("Hy-MT2-7B-Q4_K_M", models[1].Name);
        Assert.False(models[1].Installed);
        Assert.False(models[1].Downloading);
        Assert.Equal("disk full", models[1].DownloadError);
        Assert.Null(models[0].DownloadError);
    }

    [Fact]
    public async Task LlmModelActions_SendExpectedWireParams()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());

        await controller.DownloadLlmModelAsync("Hy-MT2-1.8B-UD-Q4_K_XL", CancellationToken.None);
        await controller.DeleteLlmModelAsync("Hy-MT2-7B-Q6_K", CancellationToken.None);
        await controller.SetLlmModelAsync("Hy-MT2-1.8B-UD-Q4_K_XL", CancellationToken.None);

        Assert.Equal(3, peer.Calls.Count);
        Assert.Equal("llm.models.download", peer.Calls[0].Method);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", peer.Calls[0].Params!["name"]!.GetValue<string>());
        Assert.Equal("llm.models.delete", peer.Calls[1].Method);
        Assert.Equal("Hy-MT2-7B-Q6_K", peer.Calls[1].Params!["name"]!.GetValue<string>());
        Assert.Equal("llm.models.set", peer.Calls[2].Method);
        Assert.Equal("Hy-MT2-1.8B-UD-Q4_K_XL", peer.Calls[2].Params!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task LlmServerStatus_MapsWireShape()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "llm.server.status"
            ? new JsonObject
            {
                ["state"] = "error", ["model"] = "Hy-MT2-1.8B-Q4_K_M",
                ["error"] = "health timeout", ["attempt"] = 42L, ["phase"] = "finalizing",
                ["bytesLoaded"] = 100L, ["bytesTotal"] = 400L, ["bytesPerSec"] = 12.5,
                ["progressAvailable"] = true,
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        LlmServerStatusInfo status = await controller.GetLlmServerStatusAsync(CancellationToken.None);

        Assert.Equal("llm.server.status", Assert.Single(peer.Calls).Method);
        Assert.Equal("error", status.State);
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M", status.Model);
        Assert.Equal("health timeout", status.Error);
        Assert.Equal(42, status.Attempt);
        Assert.Equal("finalizing", status.Phase);
        Assert.Equal(100, status.BytesLoaded);
        Assert.Equal(400, status.BytesTotal);
        Assert.Equal(12.5, status.BytesPerSec);
        Assert.True(status.ProgressAvailable);
    }

    [Fact]
    public async Task LlmRuntime_MapsStatusAndCheck_SendsFlavor()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method switch
        {
            "llm.runtime.status" => new JsonObject
            {
                ["tag"] = "b7123", ["installed"] = false,
                ["serverPath"] = @"C:\llm\runtime\b7123\llama-server.exe",
                ["downloading"] = true, ["downloadFailed"] = false,
                ["attempt"] = 42L, ["flavor"] = "cpu",
                ["progress"] = new JsonObject
                {
                    ["receivedBytes"] = 100L, ["totalBytes"] = 400L, ["bytesPerSec"] = 50.0,
                },
            },
            "llm.runtime.check" => new JsonObject { ["current"] = "b7123", ["latest"] = "b7250" },
            "llm.runtime.download" => new JsonObject { ["accepted"] = true, ["attempt"] = 42L },
            _ => null,
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        LlmRuntimeStatusInfo status = await controller.GetLlmRuntimeStatusAsync(CancellationToken.None, "cpu");
        Assert.Equal("cpu", peer.Calls[0].Params!["flavor"]!.GetValue<string>());
        Assert.Equal("b7123", status.Tag);
        Assert.False(status.Installed);
        Assert.Equal(@"C:\llm\runtime\b7123\llama-server.exe", status.ServerPath);
        Assert.True(status.Downloading);
        Assert.False(status.DownloadFailed);
        Assert.Equal(42, status.Attempt);
        Assert.Equal("cpu", status.Flavor);
        Assert.NotNull(status.Progress);
        Assert.Equal(100, status.Progress!.ReceivedBytes);
        Assert.Equal(400, status.Progress.TotalBytes);
        Assert.Equal(50.0, status.Progress.BytesPerSec);

        LlmRuntimeCheckInfo check = await controller.CheckLlmRuntimeAsync("cpu", CancellationToken.None);
        Assert.Equal("b7123", check.Current);
        Assert.Equal("b7250", check.Latest);
        (string Method, JsonNode? Params) checkCall = peer.Calls.Single(c => c.Method == "llm.runtime.check");
        Assert.Equal("cpu", checkCall.Params!["flavor"]!.GetValue<string>());

        long acceptedAttempt = await controller.DownloadLlmRuntimeAsync("cpu", CancellationToken.None);
        Assert.Equal(42, acceptedAttempt);
        (string method, JsonNode? @params) = peer.Calls.Last();
        Assert.Equal("llm.runtime.download", method);
        Assert.Equal("cpu", @params!["flavor"]!.GetValue<string>());
    }

    [Fact]
    public async Task LlmRuntimeStatus_MapsFailedUpdateWithPreviousInstall()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "llm.runtime.status"
            ? new JsonObject
            {
                ["tag"] = "b7123", ["installed"] = true,
                ["serverPath"] = @"C:\llm\runtime\b7123\llama-server.exe",
                ["downloading"] = false, ["downloadFailed"] = true,
                ["downloadError"] = "No llama.cpp release with a Windows x64 asset for flavor 'cpu'.",
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        LlmRuntimeStatusInfo status = await controller.GetLlmRuntimeStatusAsync(CancellationToken.None);

        Assert.True(status.Installed);
        Assert.False(status.Downloading);
        Assert.True(status.DownloadFailed);
        Assert.Equal("No llama.cpp release with a Windows x64 asset for flavor 'cpu'.", status.DownloadError);
        Assert.Null(status.Progress);
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
    public async Task OcrConfigSchema_MapsCamelCaseWireShape_ToleratingMissingOptionals()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "ocr.config.schema"
            ? new JsonObject
            {
                ["params"] = new JsonArray(
                    new JsonObject
                    {
                        ["key"] = "text_det_thresh", ["group"] = "det", ["type"] = "float",
                        ["default"] = 0.3, ["reloadRequired"] = false,
                        ["values"] = null, ["min"] = 0.0, ["max"] = 1.0,
                        ["step"] = 0.05, ["placeholder"] = "0.0 - 1.0",
                    },
                    new JsonObject
                    {
                        ["key"] = "engine", ["group"] = "device", ["type"] = "enum",
                        ["default"] = null, ["reloadRequired"] = true,
                        ["values"] = new JsonArray("paddle", "onnxruntime"),
                    },
                    new JsonObject
                    {
                        // Các trường tùy chọn thiếu hẳn → rỗng/null, không ném.
                        ["key"] = "mkldnn_cache_capacity", ["group"] = "device", ["type"] = "int",
                        ["default"] = 10, ["reloadRequired"] = true,
                    }),
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());

        IReadOnlyList<OcrParamSpec> schema = await controller.GetOcrConfigSchemaAsync(CancellationToken.None);

        (string Method, JsonNode? Params) call = Assert.Single(peer.Calls);
        Assert.Equal("ocr.config.schema", call.Method);
        Assert.Null(call.Params);

        Assert.Equal(3, schema.Count);
        OcrParamSpec det = schema[0];
        Assert.Equal("text_det_thresh", det.Key);
        Assert.Equal("det", det.Group);
        Assert.Equal("float", det.Type);
        Assert.Equal(0.3, det.Default!.GetValue<double>());
        Assert.False(det.ReloadRequired);
        Assert.Empty(det.Values);
        Assert.Equal(0.0, det.Min);
        Assert.Equal(1.0, det.Max);
        Assert.Equal(0.05, det.Step);
        Assert.Equal("0.0 - 1.0", det.Placeholder);

        OcrParamSpec engine = schema[1];
        Assert.Null(engine.Default);
        Assert.True(engine.ReloadRequired);
        Assert.Equal(["paddle", "onnxruntime"], engine.Values);
        Assert.Null(engine.Min);
        Assert.Null(engine.Max);
        Assert.Null(engine.Step);
        Assert.Null(engine.Placeholder);

        OcrParamSpec minimal = schema[2];
        Assert.Equal(10, minimal.Default!.GetValue<int>());
        Assert.Empty(minimal.Values);
        Assert.Null(minimal.Min);
        Assert.Null(minimal.Max);
        Assert.Null(minimal.Step);
        Assert.Null(minimal.Placeholder);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("scalar")]
    [InlineData("nonObjectEntry")]
    public async Task OcrConfigSchema_MalformedOrEmptyReply_ThrowsInsteadOfReturningEmptySchema(string shape)
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = _ => shape switch
        {
            "missing" => new JsonObject(),
            "empty" => new JsonObject { ["params"] = new JsonArray() },
            "scalar" => new JsonObject { ["params"] = 1 },
            _ => new JsonObject { ["params"] = new JsonArray(JsonValue.Create(1)) },
        };
        PipelineController controller = CreateController(peer, new OverlayState());

        RpcException ex = await Assert.ThrowsAsync<RpcException>(
            () => controller.GetOcrConfigSchemaAsync(CancellationToken.None));

        Assert.Equal(-32603, ex.Code);
        Assert.Contains("ocr-config-schema-invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OcrConfigSchema_PropagatesRpcError()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = _ => throw new RpcException(
            -32603, "sidecar not available (python/venv missing or spawn failed).");
        PipelineController controller = CreateController(peer, new OverlayState());

        RpcException ex = await Assert.ThrowsAsync<RpcException>(
            () => controller.GetOcrConfigSchemaAsync(CancellationToken.None));

        Assert.Equal(-32603, ex.Code);
        Assert.Equal("sidecar not available (python/venv missing or spawn failed).", ex.Message);
    }

    [Fact]
    public async Task SetOcrConfig_SendsClonedValuesTwice_AndParsesResult()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        peer.Reply = method => method == "ocr.config.set"
            ? new JsonObject
            {
                ["live"] = new JsonArray("text_det_thresh"),
                ["deferred"] = new JsonArray("cpu_threads"),
                ["reloadRequired"] = true,
            }
            : null;
        PipelineController controller = CreateController(peer, new OverlayState());
        var values = new Dictionary<string, JsonNode?>
        {
            ["text_det_thresh"] = 0.35,
            ["cpu_threads"] = 4,
        };

        OcrConfigSetResult first = await controller.SetOcrConfigAsync(values, CancellationToken.None);
        // Lần hai với CÙNG dictionary: nếu controller gắn thẳng node của caller vào params,
        // lần hai sẽ ném "The node already has a parent" — clone ở biên wire là bắt buộc.
        OcrConfigSetResult second = await controller.SetOcrConfigAsync(values, CancellationToken.None);

        Assert.Equal(["text_det_thresh"], first.Live);
        Assert.Equal(["cpu_threads"], first.Deferred);
        Assert.True(first.ReloadRequired);
        Assert.Equal(["text_det_thresh"], second.Live);

        Assert.Equal(2, peer.Calls.Count);
        foreach ((string method, JsonNode? @params) in peer.Calls)
        {
            Assert.Equal("ocr.config.set", method);
            JsonObject sent = @params!.AsObject();
            JsonObject sentParams = sent["params"]!.AsObject();
            Assert.Equal(0.35, sentParams["text_det_thresh"]!.GetValue<double>());
            Assert.Equal(4, sentParams["cpu_threads"]!.GetValue<int>());
            Assert.Null(sent["deferReload"]); // Core tự ép theo IsRunning — App không gửi
        }
        // Node của caller vẫn không có parent: settings dùng lại được ở lần set sau.
        Assert.Null(values["text_det_thresh"]!.Parent);
        Assert.Null(values["cpu_threads"]!.Parent);
    }

    [Fact]
    public async Task SetOcrConfig_EmptyMap_SendsEmptyParamsObject()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController controller = CreateController(peer, new OverlayState());

        OcrConfigSetResult result = await controller.SetOcrConfigAsync(
            new Dictionary<string, JsonNode?>(), CancellationToken.None);

        JsonObject sent = Assert.Single(peer.Calls).Params!.AsObject();
        Assert.Empty(sent["params"]!.AsObject()); // {} = khôi phục mọi mặc định (C12)
        Assert.Null(sent["deferReload"]);
        Assert.Empty(result.Live);
        Assert.Empty(result.Deferred);
        Assert.False(result.ReloadRequired);
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
    public async Task Stop_CleanupFailure_PreservesSessionAndSurfacesRetryGuidance()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        new AppSettingsService(_settingsPath).Save(AppSettings.Default);
        PipelineController controller = CreateController(peer, new OverlayState());
        var source = new SourceSelection(false, 0, null);
        await controller.StartAsync(source, CancellationToken.None);
        peer.Reply = method => method == "core.stop"
            ? throw new RpcException(-32603, "core.stop was not acknowledged") : null;

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.StopAsync(CancellationToken.None));

        Assert.Contains("core.stop", error.Message, StringComparison.Ordinal);
        Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(controller.IsRunning);
        Assert.Equal(source, controller.ActiveSource);
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

    [Fact]
    public async Task Stop_ClearsOverlayState()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        RaiseTranslatedFrame(peer);
        Assert.NotNull(overlay.Latest);

        int changed = 0;
        overlay.Changed += () => changed++;

        await controller.StopAsync(CancellationToken.None);

        // OBS Browser Source kết nối SAU khi dừng phải thấy snapshot rỗng, không dựng lại chữ
        // của phiên cũ (Spec §3 / Review Focus #3).
        Assert.Null(overlay.Latest);
        // Đường dừng do người dùng chỉ nổ StateChanged, nên phần xóa phải nổ Changed đúng một lần.
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Stop_ThenCoreExited_ClearsOverlayOnce()
    {
        // Một lần dừng có thể đi qua hai đường (StopAsync rồi core exit) → Clear phải idempotent:
        // subscriber feed chỉ nhận một payload rỗng, không phải hai.
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        RaiseTranslatedFrame(peer);

        int changed = 0;
        overlay.Changed += () => changed++;

        await controller.StopAsync(CancellationToken.None);
        peer.RaiseExited(0);

        Assert.Null(overlay.Latest);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task CoreExited_ClearsOverlayState()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        RaiseTranslatedFrame(peer);

        peer.RaiseExited(0);

        Assert.Null(overlay.Latest);
    }

    [Fact]
    public async Task PipelineStateNotification_Stopped_ClearsOverlayState()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        RaiseTranslatedFrame(peer);

        peer.RaiseNotification("pipeline.state", new JsonObject
        {
            ["running"] = false,
            ["reason"] = "sidecar-exited",
        });

        Assert.Null(overlay.Latest);
    }

    [Fact]
    public async Task PipelineStateNotification_Running_KeepsOverlayState()
    {
        // Chỉ đường chuyển sang KHÔNG chạy mới xóa; notification running:true giữa phiên mà xóa thì
        // feed sẽ nháy trắng mỗi lần Core báo nhịp.
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        RaiseTranslatedFrame(peer);

        peer.RaiseNotification("pipeline.state", new JsonObject { ["running"] = true });

        Assert.NotNull(overlay.Latest);
    }

    [Fact]
    public async Task Start_KeepsExistingOverlayState()
    {
        // Clear chỉ gắn với các đường chuyển sang không-chạy — Start không được xóa.
        var peer = new FakeCorePeer { IsStarted = true };
        var overlay = new OverlayState();
        PipelineController controller = CreateController(peer, overlay);
        overlay.Publish(new OverlayFrame(1920, 1080, DateTime.UtcNow,
            new[] { new OverlayBox(0.1, 0.1, 0.2, 0.05, "HELLO", "Xin chào") }));

        await controller.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        Assert.NotNull(overlay.Latest);
    }

    /// <summary>
    /// Pushes one <c>frame.translated</c> notification through the wired handler so the overlay has
    /// a frame to lose. Đẩy một thông báo <c>frame.translated</c> qua handler đã gắn để overlay có
    /// khung hình cần xóa.
    /// </summary>
    private static void RaiseTranslatedFrame(FakeCorePeer peer)
        => peer.RaiseNotification("frame.translated", new JsonObject
        {
            ["seq"] = 1,
            ["w"] = 1920,
            ["h"] = 1080,
            ["boxes"] = new JsonArray(
                new JsonObject
                {
                    ["x"] = 0.10, ["y"] = 0.20, ["w"] = 0.30, ["h"] = 0.05,
                    ["text"] = "HELLO", ["translated"] = "Xin chào",
                }),
        });

    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public bool StartCoreCalled { get; private set; }
        public int? CallsAtStartCore { get; private set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public Func<string, JsonNode?, Task<JsonNode?>>? RequestHandler { get; private set; }

        /// <summary>Optional per-method canned reply; null (or a null return) falls back to the default shape. Phản hồi dựng sẵn theo method; null trả về mặc định.</summary>
        public Func<string, JsonNode?>? Reply { get; set; }
        public Func<string, JsonNode?, CancellationToken, Task<JsonNode?>>? AsyncReply { get; set; }
        public event Action<string>? Called;

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
            Called?.Invoke(method);
            if (AsyncReply is not null) return AsyncReply(method, @params, ct);
            ct.ThrowIfCancellationRequested();
            JsonNode? result = (Reply?.Invoke(method)) ?? method switch
            {
                "core.start" => new JsonObject { ["running"] = true },
                "llm.server.start" => new JsonObject
                {
                    ["accepted"] = true, ["attempt"] = 1L,
                    ["model"] = @params?["name"]?.GetValue<string>(),
                },
                "llm.server.status" => new JsonObject
                {
                    ["state"] = "ready", ["model"] = "Hy-MT2-1.8B-UD-Q4_K_XL",
                    ["error"] = null, ["attempt"] = 1L, ["phase"] = "ready",
                    ["bytesLoaded"] = 8L, ["bytesTotal"] = 8L,
                    ["bytesPerSec"] = 1024.0, ["progressAvailable"] = true,
                },
                _ => new JsonObject { ["running"] = false },
            };
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
