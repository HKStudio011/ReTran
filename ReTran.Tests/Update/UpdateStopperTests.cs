using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.Update;

namespace ReTran.Tests.Update;

/// <summary>
/// Tests UpdateStopper/MauiAppShutdown on real instances: a pipeline driven by FakeCorePeer,
/// a real core process where process teardown matters, and a controllable overlay feed.
/// Kiểm thử UpdateStopper/MauiAppShutdown trên instance thật: pipeline chạy bằng FakeCorePeer,
/// tiến trình core thật khi cần kiểm chứng teardown, và overlay feed điều khiển được.
/// </summary>
public class UpdateStopperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _settingsPath;
    private readonly DbContextOptions<RetranDbContext> _options;
    private RetranDbContext? _db;

    public UpdateStopperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retran-stopper-{Guid.NewGuid():N}.db");
        _settingsPath = Path.Combine(Path.GetTempPath(), $"retran-stopper-{Guid.NewGuid():N}.json");
        _options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        using var db = new RetranDbContext(_options);
        db.Database.Migrate();
    }

    public void Dispose()
    {
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

    private static string ComSpec =>
        Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private PipelineController CreatePipeline(FakeCorePeer peer)
    {
        _db = new RetranDbContext(_options);
        return new PipelineController(
            peer,
            new AppSettingsService(_settingsPath),
            new TranslationCache(_db),
            new OverlayState(),
            NullLogger<PipelineController>.Instance);
    }

    [Fact]
    public async Task StopServices_DisposesPipelineCoreAndOverlay_InOrder()
    {
        var steps = new List<string>();
        var peer = new FakeCorePeer { IsStarted = true, StopSteps = steps };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        Assert.True(pipeline.IsRunning);

        var core = new RecordingCoreProcessClient(steps);
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);
        Assert.True(core.IsStarted);
        var overlay = new RecordingAsyncDisposable(steps);
        var stopper = new UpdateStopper(pipeline, core, overlay);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["pipeline", "core", "overlay"], steps);
        Assert.False(pipeline.IsRunning);
        Assert.False(core.IsStarted);
    }

    [Fact]
    public async Task StopServicesAsync_OverlayFailure_Propagates()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        var core = new RecordingCoreProcessClient();
        var failure = new InvalidOperationException("simulated overlay dispose failure");
        var overlay = new RecordingAsyncDisposable { ThrowOnDispose = failure };
        var stopper = new UpdateStopper(pipeline, core, overlay);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stopper.StopServicesAsync(TestContext.Current.CancellationToken));

        Assert.Same(failure, thrown);
        Assert.True(core.DisposeCalled);
        Assert.True(overlay.DisposeCalled);
    }

    [Fact]
    public async Task StopServicesAsync_Timeout_Throws()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        var core = new RecordingCoreProcessClient();
        var overlay = new RecordingAsyncDisposable { NeverCompletes = true };
        var stopper = new UpdateStopper(pipeline, core, overlay, TimeSpan.FromMilliseconds(10));

        var stopTask = stopper.StopServicesAsync(TestContext.Current.CancellationToken);
        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        Assert.Same(stopTask, completed);
        var thrown = await Assert.ThrowsAsync<TimeoutException>(() => stopTask);
        Assert.Contains("overlay", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopServicesAsync_PipelineNotRunning_SkipsStopButStillDisposesCoreAndOverlay()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer); // không StartAsync → IsRunning = false

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);
        var overlay = new RecordingAsyncDisposable();
        var stopper = new UpdateStopper(pipeline, core, overlay);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(peer.Calls);
        Assert.False(core.IsStarted);
        Assert.True(overlay.DisposeCalled);
    }

    [Fact]
    public async Task StopServicesAsync_WhenPipelineStopThrows_PropagatesAndStillDisposesCoreAndOverlay()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        peer.ThrowOnCoreStop = new InvalidOperationException("simulated core.stop failure");

        var core = new RecordingCoreProcessClient();
        var overlay = new RecordingAsyncDisposable();
        var stopper = new UpdateStopper(pipeline, core, overlay);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stopper.StopServicesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("core.stop", thrown.Message, StringComparison.Ordinal);
        Assert.True(core.DisposeCalled);
        Assert.True(overlay.DisposeCalled);
    }

    [Fact]
    public async Task StopServicesAsync_WhenCoreDisposeThrows_PropagatesAndStillDisposesOverlay()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        var core = new ThrowingCoreProcessClient();
        var overlay = new RecordingAsyncDisposable();
        var stopper = new UpdateStopper(pipeline, core, overlay);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => stopper.StopServicesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("simulated core dispose failure", thrown.Message);
        Assert.True(overlay.DisposeCalled);
    }

    [Fact]
    public async Task StopServicesAsync_CalledTwice_StopsPipelineOnlyOnce()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        peer.Calls.Clear();

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);
        var overlay = new RecordingAsyncDisposable();
        var stopper = new UpdateStopper(pipeline, core, overlay);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);
        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("core.stop", Assert.Single(peer.Calls).Method);
    }

    [Fact]
    public void MauiAppShutdown_Quit_WithoutApplication_DoesNotThrow()
    {
        // Test host không có MAUI Application — Quit() phải là no-op an toàn.
        var shutdown = new MauiAppShutdown();

        shutdown.Quit();
    }

    // ===== Test doubles =====

    /// <summary>Fake ICorePeer — ghi CallAsync, ThrowOnCoreStop ném đúng lúc StopAsync của pipeline gửi core.stop.</summary>
    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public Exception? ThrowOnCoreStop { get; set; }
        public List<string>? StopSteps { get; set; }

        public event Action<string, JsonNode?>? NotificationReceived;
        public event Action<Exception>? HandlerError;
        public event Action<int>? Exited;

        public Task StartCoreAsync(CancellationToken ct)
        {
            IsStarted = true;
            return Task.CompletedTask;
        }

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            if (method == "core.stop")
            {
                if (ThrowOnCoreStop is not null) throw ThrowOnCoreStop;
                StopSteps?.Add("pipeline");
            }
            Calls.Add((method, @params));
            return Task.FromResult<JsonNode?>(new JsonObject { ["running"] = method == "core.start" });
        }

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler) { }

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);

        public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);
    }

    private sealed class RecordingCoreProcessClient(List<string>? steps = null) : CoreProcessClient
    {
        public bool DisposeCalled { get; private set; }

        public override async ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            steps?.Add("core");
            await base.DisposeAsync();
        }
    }

    /// <summary>CoreProcessClient subclass ném đúng lúc DisposeAsync để kiểm chứng lỗi không bị nuốt.</summary>
    private sealed class ThrowingCoreProcessClient : CoreProcessClient
    {
        public override ValueTask DisposeAsync()
            => throw new InvalidOperationException("simulated core dispose failure");
    }

    private sealed class RecordingAsyncDisposable(List<string>? steps = null) : IAsyncDisposable
    {
        public bool DisposeCalled { get; private set; }
        public Exception? ThrowOnDispose { get; set; }
        public bool NeverCompletes { get; set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            steps?.Add("overlay");
            if (ThrowOnDispose is not null) throw ThrowOnDispose;
            return NeverCompletes
                ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan))
                : ValueTask.CompletedTask;
        }
    }
}
