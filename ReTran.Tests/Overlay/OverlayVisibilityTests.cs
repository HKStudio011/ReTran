using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;

namespace ReTran.Tests;

// ShouldShowObs == settings.OverlayOnGame && pipeline.IsRunning, và Changed chỉ nổ khi GIÁ trị đổi
// (spec K8/5.14). Pipeline được dựng thật với fake peer (pattern trong PipelineControllerTests) để
// cả hai đường dừng — StopAsync (chỉ StateChanged) và Core dừng đột xuất (PipelineStateChanged rồi
// StateChanged) — đi qua đúng như ngoài app.
public sealed class OverlayVisibilityTests : IDisposable
{
    private readonly string _settingsPath;
    private readonly AppSettingsService _settings;

    public OverlayVisibilityTests()
    {
        _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-visibility-{Guid.NewGuid():N}.json");
        _settings = new AppSettingsService(_settingsPath);
    }

    public void Dispose()
    {
        try { File.Delete(_settingsPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A real controller whose core peer is a stub: starting and stopping only move bookkeeping, no
    /// process and no shared memory. Một controller thật với peer giả: start/stop chỉ di chuyển
    /// bookkeeping, không có tiến trình và không có shared memory.
    /// </summary>
    private PipelineController NewController(OverlayState overlay, FakePeer peer)
    {
        // Context không bao giờ được truy vấn ở đây (không có notification translate.*), nên
        // không cần migrate; file DB sẽ không tồn tại.
        var options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), $"retran-visibility-{Guid.NewGuid():N}.db")};Pooling=False")
            .Options;
        return new PipelineController(
            peer,
            _settings,
            new TranslationCache(new RetranDbContext(options)),
            overlay,
            NullLogger<PipelineController>.Instance);
    }

    private static void SaveOverlay(AppSettingsService settings, bool on)
        => settings.Save(AppSettings.Default with { OverlayOnGame = on });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Matrix_ShouldShowObs_IsOverlaySwitchAndRunning(bool overlayOnGame, bool startPipeline)
    {
        SaveOverlay(_settings, overlayOnGame);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        if (startPipeline)
        {
            await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        }

        Assert.Equal(overlayOnGame && startPipeline, visibility.ShouldShowObs);
    }

    [Fact]
    public async Task Ctor_ComputesCurrentValue_NoEventNeeded()
    {
        // Server feed dựng sau khi pipeline đã chạy: giá trị phải đúng ngay từ ctor, nếu không nó
        // sẽ coi overlay là ẩn cho tới event đầu tiên.
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        Assert.True(visibility.ShouldShowObs);
    }

    [Fact]
    public async Task SettingsSaved_TogglingOverlayOnce_RaisesChangedOnce()
    {
        SaveOverlay(_settings, false);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        SaveOverlay(_settings, true);
        SaveOverlay(_settings, true); // cùng giá trị ⇒ Save vẫn nổ AppSettingsService.Changed, gate phải chặn

        Assert.True(visibility.ShouldShowObs);
        Assert.Equal(1, changed);

        SaveOverlay(_settings, false);
        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task SettingsSaved_WithoutChangingOverlay_RaisesNothing()
    {
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        _settings.Save(AppSettings.Default with { OverlayOnGame = true, Dst = "en" });
        _settings.Save(AppSettings.Default with { OverlayOnGame = true, CaptureFps = 30 });

        Assert.True(visibility.ShouldShowObs);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task PipelineStarted_WithOverlayOn_RaisesChangedOnce()
    {
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        Assert.True(pipeline.IsRunning);
        Assert.True(visibility.ShouldShowObs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task UserStop_RaisesChangedOnce()
    {
        // Đường người dùng bấm Stop: PipelineController chỉ nổ StateChanged (:297-300).
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        await pipeline.StopAsync(CancellationToken.None);

        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task CoreSideStop_TwoPipelineEvents_RaisesChangedOnce()
    {
        // Đường Core báo dừng: một thông báo pipeline.state nổ PipelineStateChanged RỒI StateChanged
        // (:839-842) ⇒ listener chỉ được tính một lần theo edge của ShouldShowObs.
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        peer.RaiseNotification("pipeline.state", new JsonObject
        {
            ["running"] = false,
            ["reason"] = "sidecar-exited",
        });

        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task CoreExited_RaisesChangedOnce()
    {
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        peer.RaiseExited(0);

        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Stop_ThenCoreExited_RaisesChangedOnce()
    {
        // Hai đường dừng chồng nhau (StopAsync rồi core exit) ⇒ vẫn chỉ một edge ẩn.
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        await pipeline.StopAsync(CancellationToken.None);
        peer.RaiseExited(0);

        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Stop_WithOverlayAlreadyOff_RaisesNothing()
    {
        // Overlay đã tắt thì ShouldShowObs đã false trước đó; dừng pipeline không đổi giá trị ⇒
        // không có payload xóa thứ hai (feed cũng đã im từ trước).
        SaveOverlay(_settings, false);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        await pipeline.StopAsync(CancellationToken.None);

        Assert.False(visibility.ShouldShowObs);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task Restart_RaisesShownEdgeAgain()
    {
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        using var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        var edges = new List<bool>();
        visibility.Changed += () => edges.Add(visibility.ShouldShowObs);

        await pipeline.StopAsync(CancellationToken.None);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);

        Assert.True(visibility.ShouldShowObs);
        Assert.Equal([false, true], edges);
    }

    [Fact]
    public async Task Dispose_UnsubscribesEverySource_AndIsIdempotent()
    {
        SaveOverlay(_settings, true);
        var peer = new FakePeer { IsStarted = true };
        PipelineController pipeline = NewController(new OverlayState(), peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), CancellationToken.None);
        var visibility = new OverlayVisibility(_settings, pipeline, NullLogger<OverlayVisibility>.Instance);

        int changed = 0;
        visibility.Changed += () => changed++;

        visibility.Dispose();
        visibility.Dispose();

        // Mọi nguồn re-evaluate phải bị gỡ: không Save, không stop, không exit nào còn nổ event.
        // Giá trị vẫn đọc trực tiếp từ pipeline (đã dừng) — dispose là ngừng thông báo, không đóng băng
        // ShouldShowObs.
        // Every re-evaluation source must be unsubscribed: no Save, no stop, no exit raises anything.
        // The value is still read live from the pipeline (now stopped) — dispose stops notifications, it
        // does not freeze ShouldShowObs.
        SaveOverlay(_settings, false);
        await pipeline.StopAsync(CancellationToken.None);
        peer.RaiseExited(0);

        Assert.Equal(0, changed);
        Assert.False(visibility.ShouldShowObs);
    }

    [Fact]
    public void Ctor_NullArguments_Throws()
    {
        var settings = new AppSettingsService(_settingsPath);
        var pipeline = NewController(new OverlayState(), new FakePeer { IsStarted = true });
        Assert.Throws<ArgumentNullException>(() => new OverlayVisibility(null!, pipeline, NullLogger<OverlayVisibility>.Instance));
        Assert.Throws<ArgumentNullException>(() => new OverlayVisibility(settings, null!, NullLogger<OverlayVisibility>.Instance));
        Assert.Throws<ArgumentNullException>(() => new OverlayVisibility(settings, pipeline, null!));
    }

    private sealed class FakePeer : ICorePeer
    {
        public bool IsStarted { get; set; }

        public event Action<string, JsonNode?>? NotificationReceived;
        public event Action<Exception>? HandlerError;
        public event Action<int>? Exited;

        public Task StartCoreAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
            => Task.FromResult<JsonNode?>(new JsonObject { ["running"] = method == "core.start" });

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler) { }

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);

        public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);
    }
}
