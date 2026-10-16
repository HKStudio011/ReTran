using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;

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

    private PipelineController CreateController(FakeCorePeer peer, OverlayState overlay)
    {
        _controllerDb = new RetranDbContext(_options);
        return new PipelineController(
            peer,
            new AppSettingsService(_settingsPath),
            new TranslationCache(_controllerDb),
            overlay,
            NullLogger<PipelineController>.Instance);
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

        Assert.Null(call.Params["ocrFps"]);
        Assert.Null(call.Params["captureFps"]);
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

    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public bool StartCoreCalled { get; private set; }
        public int? CallsAtStartCore { get; private set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public Func<string, JsonNode?, Task<JsonNode?>>? RequestHandler { get; private set; }

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
            JsonNode? result = method == "core.start"
                ? new JsonObject { ["running"] = true }
                : new JsonObject { ["running"] = false };
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
