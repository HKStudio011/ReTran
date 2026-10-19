using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReTran_App.Core;
using ReTran_App.Data;
using ReTran_App.Services;
using ReTran_App.Services.Update;

namespace ReTran.Tests.Update;

/// <summary>
/// Kiểm thử UpdateStopper/MauiAppShutdown trên instance thật: pipeline chạy bằng FakeCorePeer,
/// core process thật (cmd.exe sống mãi với --serve + stdin redirect) — xác nhận đủ 2 bước, bỏ qua
/// StopAsync khi pipeline tắt, mỗi bước throw không chặn bước sau.
/// Tests for UpdateStopper/MauiAppShutdown on real instances: pipeline driven by FakeCorePeer and a
/// real core process (cmd.exe stays alive with --serve + redirected stdin) — all 2 steps run,
/// StopAsync skipped when the pipeline is off, a throwing step never blocks the next.
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
    public async Task StopServicesAsync_PipelineRunning_StopsPipelineAndDisposesCore()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        Assert.True(pipeline.IsRunning);
        peer.Calls.Clear();

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);
        Assert.True(core.IsStarted);

        var stopper = new UpdateStopper(pipeline, core, NullLogger<UpdateStopper>.Instance);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("core.stop", Assert.Single(peer.Calls).Method);
        Assert.False(pipeline.IsRunning);
        Assert.False(core.IsStarted);   // bước (2) đã dispose core (kill tree)
    }

    [Fact]
    public async Task StopServicesAsync_PipelineNotRunning_SkipsStopButStillDisposesCore()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer); // không StartAsync → IsRunning = false

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);

        var stopper = new UpdateStopper(pipeline, core, NullLogger<UpdateStopper>.Instance);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(peer.Calls);       // bỏ qua StopAsync — không gửi core.stop
        Assert.False(core.IsStarted);   // core vẫn được dispose
    }

    [Fact]
    public async Task StopServicesAsync_WhenPipelineStopThrows_WarnsAndStillDisposesCore()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        peer.ThrowOnCoreStop = new InvalidOperationException("simulated core.stop failure");

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);

        var log = new RecordingLogger<UpdateStopper>();
        var stopper = new UpdateStopper(pipeline, core, log);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken); // không ném

        Assert.Single(log.Warnings);    // nuốt exception + log warn
        Assert.False(core.IsStarted);   // bước (1) throw nhưng bước (2) vẫn chạy
    }

    [Fact]
    public async Task StopServicesAsync_WhenCoreDisposeThrows_DoesNotThrow()
    {
        // Bước (2) throw → nuốt + log warn, method không ném (bước cuối — không còn bước sau).
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);

        var core = new ThrowingCoreProcessClient();
        var log = new RecordingLogger<UpdateStopper>();
        var stopper = new UpdateStopper(pipeline, core, log);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken); // không ném

        Assert.Single(log.Warnings);    // nuốt exception + log warn
    }

    [Fact]
    public async Task StopServicesAsync_CalledTwice_SecondCallIsNoOp()
    {
        var peer = new FakeCorePeer { IsStarted = true };
        PipelineController pipeline = CreatePipeline(peer);
        await pipeline.StartAsync(new SourceSelection(false, 0, null), TestContext.Current.CancellationToken);
        peer.Calls.Clear();

        var core = new CoreProcessClient();
        await core.StartAsync(ComSpec, TestContext.Current.CancellationToken);
        var stopper = new UpdateStopper(pipeline, core, NullLogger<UpdateStopper>.Instance);

        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);
        await stopper.StopServicesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("core.stop", Assert.Single(peer.Calls).Method); // lần 2 IsRunning=false → skip
    }

    [Fact]
    public void MauiAppShutdown_Quit_WithoutApplication_DoesNotThrow()
    {
        // Test host không có MAUI Application — Quit() phải là no-op an toàn.
        var shutdown = new MauiAppShutdown();

        shutdown.Quit(); // không ném
    }

    // ===== Test doubles =====

    /// <summary>Fake ICorePeer — ghi CallAsync, ThrowOnCoreStop ném đúng lúc StopAsync của pipeline gửi core.stop.</summary>
    private sealed class FakeCorePeer : ICorePeer
    {
        public bool IsStarted { get; set; }
        public List<(string Method, JsonNode? Params)> Calls { get; } = new();
        public Exception? ThrowOnCoreStop { get; set; }

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
            if (method == "core.stop" && ThrowOnCoreStop is not null) throw ThrowOnCoreStop;
            Calls.Add((method, @params));
            return Task.FromResult<JsonNode?>(new JsonObject { ["running"] = method == "core.start" });
        }

        public void SetRequestHandler(Func<string, JsonNode?, Task<JsonNode?>> handler) { }

        public void RaiseNotification(string method, JsonNode? @params)
            => NotificationReceived?.Invoke(method, @params);

        public void RaiseHandlerError(Exception ex) => HandlerError?.Invoke(ex);

        public void RaiseExited(int exitCode) => Exited?.Invoke(exitCode);
    }

    /// <summary>CoreProcessClient subclass ném đúng lúc DisposeAsync — kiểm chứng bước (2) được nuốt + log warn.</summary>
    private sealed class ThrowingCoreProcessClient : CoreProcessClient
    {
        public override ValueTask DisposeAsync()
            => throw new InvalidOperationException("simulated core dispose failure");
    }

    /// <summary>ILogger ghi lại message ≥ Warning để xác nhận bước throw bị nuốt + log warn.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
