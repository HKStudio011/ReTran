using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.OverlayWindow;

namespace ReTran.Tests;

public class OverlayWindowServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _settingsPath;
    private readonly DbContextOptions<RetranDbContext> _options;

    private RetranDbContext? _db;
    private OverlayWindowService? _serviceInstance;
    private OverlayState _overlay = null!;
    private FakeCorePeer _peer = null!;
    private FakeHost _host = null!;
    private FakeTracker _tracker = null!;
    private FakeForegroundWatcher _foreground = null!;
    private PipelineController _pipeline = null!;

    public OverlayWindowServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retran-overlay-{Guid.NewGuid():N}.db");
        _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-overlay-{Guid.NewGuid():N}.json");
        _options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        using var db = new RetranDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        _serviceInstance?.Dispose();
        _db?.Dispose();
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

    private OverlayWindowService CreateService(TimeSpan? redrawInterval = null)
    {
        _overlay = new OverlayState();
        _peer = new FakeCorePeer { IsStarted = true };
        _db = new RetranDbContext(_options);
        _pipeline = new PipelineController(
            _peer,
            new AppSettingsService(_settingsPath),
            new TranslationCache(_db),
            _overlay,
            NullLogger<PipelineController>.Instance);
        _host = new FakeHost();
        _tracker = new FakeTracker();
        _foreground = new FakeForegroundWatcher();
        _serviceInstance = new OverlayWindowService(
            _overlay, _pipeline, _host, _tracker, _foreground,
            new AppSettingsService(_settingsPath),
            NullLogger<OverlayWindowService>.Instance,
            redrawInterval);
        return _serviceInstance;
    }

    private async Task StartPipelineAsync() =>
        await _pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

    private async Task StartWindowPipelineAsync() =>
        await _pipeline.StartAsync(new SourceSelection(true, 0, "0x1234"), CancellationToken.None);

    static OverlayFrame Frame(params (double X, double Y, double W, double H, string Text, string Translated)[] boxes) =>
        new(1920, 1080, DateTime.UtcNow, boxes.Select(b => new OverlayBox(b.X, b.Y, b.W, b.H, b.Text, b.Translated)).ToList());

    [Fact]
    public void SetEnabled_True_CreatesHostAndPersists()
    {
        OverlayWindowService service = CreateService();

        service.SetEnabled(true);

        Assert.True(service.IsEnabled);
        Assert.Equal(1, _host.CreateCalls);
        Assert.Contains("\"overlayOnGame\": true", File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void SetEnabled_False_HidesHostWithoutDisposingIt()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);

        service.SetEnabled(false);

        Assert.False(service.IsEnabled);
        Assert.True(_host.HideCalls >= 1);
        Assert.Equal(0, _host.DisposeCalls);
        Assert.Contains("\"overlayOnGame\": false", File.ReadAllText(_settingsPath));
    }

    [Fact]
    public async Task EnableDisableEnable_ShowsOverlayAgain()
    {
        OverlayWindowService service = CreateService();
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();

        service.SetEnabled(true);
        service.TickAnchor();
        service.SetEnabled(false);
        service.SetEnabled(true);
        service.TickAnchor();

        Assert.Equal(2, _host.Shows.Count);
    }

    [Fact]
    public async Task TickAnchor_WindowSourceNotForeground_Hides()
    {
        OverlayWindowService service = CreateService();
        _tracker.Rect = (100, 100, 1280, 720);
        _tracker.Foreground = false;
        await StartWindowPipelineAsync();

        service.SetEnabled(true);
        service.TickAnchor();

        Assert.True(_host.HideCalls >= 1);
        Assert.Empty(_host.Shows);
    }

    [Fact]
    public async Task TickAnchor_WindowRegainsForeground_ShowsAgain()
    {
        OverlayWindowService service = CreateService();
        _tracker.Rect = (100, 100, 1280, 720);
        _tracker.Foreground = false;
        await StartWindowPipelineAsync();
        service.SetEnabled(true);
        service.TickAnchor();

        _tracker.Foreground = true;
        service.TickAnchor();

        Assert.True(_host.HideCalls >= 1);
        Assert.Equal((100, 100, 1280, 720), Assert.Single(_host.Shows));
    }

    [Fact]
    public async Task TickAnchor_ScreenSource_ShowsWhenTrackerAllows()
    {
        OverlayWindowService service = CreateService();
        _tracker.Rect = (0, 0, 1920, 1080);
        _tracker.Foreground = false;
        await StartPipelineAsync();

        service.SetEnabled(true);
        service.TickAnchor();

        Assert.Equal((0, 0, 1920, 1080), Assert.Single(_host.Shows));
    }

    [Fact]
    public async Task ForegroundWatcher_Raise_HidesWithoutWaitingForPoll()
    {
        OverlayWindowService service = CreateService();
        _tracker.Rect = (100, 100, 1280, 720);
        await StartWindowPipelineAsync();
        service.SetEnabled(true);
        service.TickAnchor();

        _tracker.Foreground = false;
        _foreground.Raise(new nint(0x1234));

        Assert.True(_host.HideCalls >= 1);
    }

    [Fact]
    public void Dispose_DisposesHostAndWatcher()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);

        service.Dispose();

        Assert.Equal(1, _host.DisposeCalls);
        Assert.True(_foreground.Disposed);
    }

    [Fact]
    public void PublishFrame_BeforeAnchor_NoDraw()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);

        _overlay.Publish(Frame((0.5, 0.5, 0.2, 0.1, "A", "B")));

        Assert.Empty(_host.Draws);
    }

    [Fact]
    public async Task TickAnchor_ThenPublish_DrawsLaidOutBoxes()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();

        service.TickAnchor();
        _overlay.Publish(Frame((0.5, 0.5, 0.2, 0.1, "A", "B")));

        Assert.Equal((0, 0, 1280, 720), Assert.Single(_host.Shows));
        IReadOnlyList<DrawBox> boxes = Assert.Single(_host.Draws);
        DrawBox box = Assert.Single(boxes);
        Assert.Equal(new DrawBox(640, 360, 256, 72, "B"), box);
    }

    [Fact]
    public async Task TickAnchor_SameRect_NoRepeatedShow()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();

        service.TickAnchor();
        service.TickAnchor();

        Assert.Single(_host.Shows);
    }

    [Fact]
    public async Task TickAnchor_TrackerNull_Hides()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = null;
        await StartPipelineAsync();

        service.TickAnchor();

        Assert.True(_host.HideCalls >= 1);
    }

    [Fact]
    public void TickAnchor_NoActiveSource_Hides()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);

        service.TickAnchor();

        Assert.True(_host.HideCalls >= 1);
    }

    [Fact]
    public async Task PublishTwiceWithinInterval_DrawsOnce()
    {
        OverlayWindowService service = CreateService(TimeSpan.FromHours(1));
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();
        service.TickAnchor();

        _overlay.Publish(Frame((0.5, 0.5, 0.2, 0.1, "A", "B")));
        _overlay.Publish(Frame((0.5, 0.5, 0.2, 0.1, "A", "B2")));

        Assert.Single(_host.Draws);
    }

    [Fact]
    public async Task PublishFrame_WithNoBoxes_DrawsEmptyList()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();
        service.TickAnchor();

        _overlay.Publish(Frame());

        IReadOnlyList<DrawBox> boxes = Assert.Single(_host.Draws);
        Assert.Empty(boxes);
    }

    [Fact]
    public async Task CoreExited_HidesOverlay()
    {
        OverlayWindowService service = CreateService();
        service.SetEnabled(true);
        _tracker.Rect = (0, 0, 1280, 720);
        await StartPipelineAsync();
        service.TickAnchor();
        int hidesBefore = _host.HideCalls;

        _peer.RaiseExited(0);

        Assert.True(_host.HideCalls > hidesBefore);
    }

    private sealed class FakeHost : IOverlayHost
    {
        public bool IsCreated { get; private set; }
        public int CreateCalls, HideCalls, DisposeCalls;
        public List<(int X, int Y, int W, int H)> Shows = new();
        public List<IReadOnlyList<DrawBox>> Draws = new();

        public void Create()
        {
            IsCreated = true;
            CreateCalls++;
        }

        public void Show(int x, int y, int width, int height) => Shows.Add((x, y, width, height));

        public void Hide() => HideCalls++;

        public void Draw(IReadOnlyList<DrawBox> boxes) => Draws.Add(boxes);

        public void Dispose() => DisposeCalls++;
    }

    private sealed class FakeTracker : IAnchorTracker
    {
        public (int X, int Y, int Width, int Height)? Rect;
        public bool Foreground = true;

        public (int X, int Y, int Width, int Height)? GetAnchorRect(SourceSelection source) => Rect;

        public bool IsSourceForeground(SourceSelection source, nint? foregroundWindow = null) =>
            !source.UseWindow || Foreground;
    }

    private sealed class FakeForegroundWatcher : IForegroundWatcher
    {
        private Action<nint>? _callback;

        public bool Disposed { get; private set; }

        public void Start(Action<nint> onForegroundChanged) => _callback = onForegroundChanged;

        public void Raise(nint hwnd) => _callback?.Invoke(hwnd);

        public void Dispose()
        {
            Disposed = true;
            _callback = null;
        }
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
