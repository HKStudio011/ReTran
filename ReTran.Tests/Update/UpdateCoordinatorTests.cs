using Microsoft.Extensions.Logging;
using ReTran_App.Services.Update;

namespace ReTran.Tests.Update;

/// <summary>
/// Kiểm thử pipeline cập nhật: download → verify → stop services → install → quit —
/// thứ tự bắt buộc, mọi nhánh fail/cancel, guard re-entrancy theo runId.
/// Tests for the update pipeline: download → verify → stop services → install → quit —
/// required order, every fail/cancel branch, runId-based re-entrancy guard.
/// </summary>
public class UpdateCoordinatorTests
{
    private static ReleaseInfo Release() =>
        new("v9.9.9", "https://github.com/HKStudio011/retran/releases/tag/v9.9.9", null, []);

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), $"điều kiện không đạt trong 5s: {because}");
    }

    private static Fixtures CreateFixtures()
    {
        var calls = new List<string>();
        var updates = new FakeUpdateService(calls);
        var stopper = new FakeStopper(calls);
        var shutdown = new FakeShutdown(calls);
        var recovery = new FakeRecovery(calls);
        var log = new RecordingLogger<UpdateCoordinator>();
        var coordinator = new UpdateCoordinator(updates, stopper, shutdown, log, recovery);
        return new Fixtures(coordinator, updates, stopper, shutdown, recovery, log, calls);
    }

    [Fact]
    public async Task StartUpdateAsync_HappyPath_StopsServicesThenInstallsThenQuits()
    {
        // Thứ tự bắt buộc: download → verify → stop → install → quit
        using var fx = CreateFixtures();
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(["download", "verify", "stop", "install", "quit"], fx.Calls);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenDownloadFails_RaisesFailedDownload_ServicesNotStopped()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        fx.Updates.DownloadTcs.SetResult(null);

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.DownloadFailed, Assert.Single(failed));
        Assert.False(fx.Stopper.StopCalled);
        Assert.DoesNotContain("recover", fx.Calls);
        Assert.False(fx.Coordinator.UpdateInProgress);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenVerifyFails_RaisesFailedVerify_ServicesNotStopped()
    {
        // Verify chạy TRƯỚC stop — zip hỏng không được phép kéo services theo
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        fx.Updates.VerifyResult = false;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.VerifyFailed, Assert.Single(failed));
        Assert.False(fx.Stopper.StopCalled);
        Assert.DoesNotContain("install", fx.Calls);
        Assert.DoesNotContain("recover", fx.Calls);
        Assert.False(fx.Coordinator.UpdateInProgress);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenStopServicesFails_RestartsCurrentAppThenQuitsWithoutUpdater()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += error =>
        {
            failed.Add(error);
            fx.Calls.Add("failed");
        };
        fx.Stopper.ThrowOnStop = new InvalidOperationException("simulated OverlayFeedServer.DisposeAsync failure");
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.StopServicesFailed, Assert.Single(failed));
        Assert.Equal(["download", "verify", "stop", "failed", "recover", "quit"], fx.Calls);
        Assert.True(fx.Coordinator.UpdateInProgress);
        Assert.True(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenInstallReturnsFalse_RestartsCurrentAppThenQuits()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += error =>
        {
            failed.Add(error);
            fx.Calls.Add("failed");
        };
        fx.Updates.InstallResult = false;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.InstallFailed, Assert.Single(failed));
        Assert.Equal(["download", "verify", "stop", "install", "failed", "recover", "quit"], fx.Calls);
        Assert.True(fx.Coordinator.UpdateInProgress);
        Assert.True(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenInstallThrows_RestartsCurrentAppThenQuits()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += error =>
        {
            failed.Add(error);
            fx.Calls.Add("failed");
        };
        fx.Updates.ThrowOnInstall = new InvalidOperationException("simulated updater launch failure");
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.InstallFailed, Assert.Single(failed));
        Assert.Equal(["download", "verify", "stop", "install", "failed", "recover", "quit"], fx.Calls);
        Assert.True(fx.Coordinator.UpdateInProgress);
        Assert.True(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenRecoveryRestartFails_LogsAndStillQuitsWithoutThrowing()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        fx.Stopper.ThrowOnStop = new TimeoutException("simulated stop timeout");
        fx.Recovery.ThrowOnRestart = true;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.StopServicesFailed, Assert.Single(failed));
        Assert.Equal(["download", "verify", "stop", "recover", "quit"], fx.Calls);
        Assert.Contains(fx.Log.Warnings, warning =>
            warning.Contains("stop services failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fx.Log.Warnings, warning =>
            warning.Contains("restart failed", StringComparison.OrdinalIgnoreCase));
        Assert.True(fx.Coordinator.UpdateInProgress);
        Assert.True(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task Dispose_BeforePostStopRecovery_AbortsSilentlyWithoutFailedOrRestart()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += error =>
        {
            failed.Add(error);
            fx.Calls.Add("failed");
        };
        fx.Stopper.ThrowOnStop = new CallbackException(() =>
        {
            fx.Calls.Add("dispose");
            fx.Coordinator.Dispose();
            return "simulated stop failure with Dispose before recovery";
        });
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Empty(failed);
        Assert.Equal(["download", "verify", "stop", "dispose"], fx.Calls);
        Assert.DoesNotContain("recover", fx.Calls);
        Assert.DoesNotContain("quit", fx.Calls);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task Dispose_FromFailedDuringPostStopFailure_AbortsRestartAndDoesNotThrow()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += error =>
        {
            failed.Add(error);
            fx.Calls.Add("failed");
            fx.Coordinator.Dispose();
            fx.Calls.Add("dispose");
            throw new InvalidOperationException("simulated UI failure after Dispose");
        };
        fx.Stopper.ThrowOnStop = new InvalidOperationException("simulated stop failure");
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.StopServicesFailed, Assert.Single(failed));
        Assert.Equal(["download", "verify", "stop", "failed", "dispose"], fx.Calls);
        Assert.DoesNotContain("recover", fx.Calls);
        Assert.DoesNotContain("quit", fx.Calls);
        Assert.Contains(fx.Log.Warnings, warning =>
            warning.Contains("UI notification failed", StringComparison.OrdinalIgnoreCase));
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Coordinator.IsFinalizing);
    }

    [Fact]
    public async Task CancelUpdate_BeforeFinalize_CancelsAndRaisesCancelled()
    {
        using var fx = CreateFixtures();
        var cancelled = 0;
        var failed = new List<UpdateError>();
        fx.Coordinator.Cancelled += () => cancelled++;
        fx.Coordinator.Failed += failed.Add;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Calls.Contains("download"), "download phải bắt đầu");
        fx.Coordinator.CancelUpdate();
        Assert.True(fx.Updates.DownloadCancellationToken.IsCancellationRequested);
        // Giải phóng download còn treo để pipeline thoát (check IsCancellationRequested sau await)
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));
        await startTask;

        Assert.Equal(1, cancelled);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Stopper.StopCalled);
        Assert.Empty(failed);
    }

    [Fact]
    public async Task CancelUpdate_DuringFinalize_IsNoOp()
    {
        // Finalise = stop services → install → quit: không được huỷ giữa chừng
        using var fx = CreateFixtures();
        var cancelled = 0;
        fx.Coordinator.Cancelled += () => cancelled++;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));
        var stopBlock = fx.Stopper.BlockStop();

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Coordinator.IsFinalizing, "pipeline phải vào finalise");
        fx.Coordinator.CancelUpdate();

        Assert.Equal(0, cancelled);
        Assert.True(fx.Coordinator.UpdateInProgress);

        stopBlock.SetResult(true);
        await startTask;

        // Cancel là no-op — pipeline chạy trọn tới quit
        Assert.Equal(["download", "verify", "stop", "install", "quit"], fx.Calls);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenAlreadyInProgress_Throws()
    {
        using var fx = CreateFixtures();
        var first = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Calls.Contains("download"), "update đầu tiên phải đang chạy");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.Coordinator.StartUpdateAsync(Release()));

        fx.Coordinator.CancelUpdate();
        fx.Updates.DownloadTcs.SetResult(null);
        await first;

        Assert.False(fx.Stopper.StopCalled);
        Assert.False(fx.Coordinator.UpdateInProgress);
    }

    [Fact]
    public async Task Dispose_CancelsPendingUpdate()
    {
        using var fx = CreateFixtures();
        var cancelled = 0;
        fx.Coordinator.Cancelled += () => cancelled++;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Calls.Contains("download"), "update phải đang chạy");
        fx.Coordinator.Dispose();

        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Coordinator.IsFinalizing);
        // Dispose là teardown của app, không phải hành động người dùng — không bắn Cancelled
        Assert.Equal(0, cancelled);
        Assert.False(fx.Stopper.StopCalled);

        fx.Updates.DownloadTcs.SetResult(null);
        await startTask; // không ném exception
    }

    [Fact]
    public async Task StartUpdateAsync_WhenQuitThrows_ResetsStateWithoutFailed()
    {
        // Quit lỗi = process vẫn sống — update đã thành công, chỉ Warn + reset, KHÔNG Failed
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        fx.Shutdown.ThrowOnQuit = true;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Empty(failed);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Coordinator.IsFinalizing);
        Assert.Contains(fx.Log.Warnings, w => w.Contains("quit failed"));
        Assert.Equal(1, fx.Shutdown.QuitCalled);
        Assert.DoesNotContain("recover", fx.Calls);
    }

    [Fact]
    public async Task StartUpdateAsync_HandlerReentrantStartDuringFailed_UsesRunIdGuard()
    {
        // Release trailing của run 1 (Fail → handler Dispose + Start run 2) là no-op theo runId —
        // không được reset/kẹt trạng thái của run 2.
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        var handlerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? startRun2 = null;

        fx.Coordinator.Failed += _ =>
        {
            if (startRun2 is not null)
            {
                return;
            }

            fx.Updates.DownloadTcs =
                new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            fx.Coordinator.Dispose();
            startRun2 = fx.Coordinator.StartUpdateAsync(Release());
            handlerDone.TrySetResult();
        };

        var startRun1 = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Calls.Contains("download"), "run 1 download phải bắt đầu");
        fx.Updates.DownloadTcs.SetResult(null);

        await WaitUntilAsync(() => failed.Count == 1, "run 1 fail phải bắn Failed");
        await handlerDone.Task;
        await startRun1; // trailing của run 1 (Reset runId) đã chạy xong khi task hoàn tất

        Assert.True(fx.Coordinator.UpdateInProgress, "run 2 không được reset bởi trailing của run 1");

        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));
        await startRun2!;

        // run 1: download fail · run 2: download → verify → stop → install → quit chạy trọn
        Assert.Equal(["download", "download", "verify", "stop", "install", "quit"], fx.Calls);
        Assert.Single(failed);
    }

    [Fact]
    public async Task Dispose_DuringStopServices_AbortsSilentlyWithoutFailed()
    {
        // Dispose thắng trong window stop-services (cooperative stopper ném OCE theo token):
        // abort IM LẶNG — Warn + reset, KHÔNG Failed giữa lúc app đang teardown.
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));
        fx.Stopper.BlockStop();

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Coordinator.IsFinalizing, "pipeline phải vào finalise");

        fx.Coordinator.Dispose();
        await startTask; // thoát im lặng, không ném exception

        Assert.Empty(failed);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Coordinator.IsFinalizing);
        Assert.DoesNotContain("install", fx.Calls);
        Assert.DoesNotContain("recover", fx.Calls);
        Assert.DoesNotContain("quit", fx.Calls);
        Assert.Contains(fx.Log.Warnings, w =>
            w.Contains("stopping services", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StartUpdateAsync_StaleRunDownloadFault_DoesNotClobberNewRun()
    {
        // Run 1 fault download SAU khi CancelUpdate + run 2 đã start: catch của run 1 phải thoát
        // im lặng — không bắn Failed, không huỷ CTS/reset trạng thái của run 2.
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;
        var run1Tcs = fx.Updates.DownloadTcs;

        var startRun1 = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Calls.Contains("download"), "run 1 download phải bắt đầu");

        fx.Coordinator.CancelUpdate();
        fx.Updates.DownloadTcs =
            new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRun2 = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(
            () => fx.Calls.Count(c => c == "download") == 2, "run 2 download phải bắt đầu");

        run1Tcs.SetException(new InvalidOperationException("late download fault"));
        await startRun1; // run 1 thoát im lặng

        Assert.Empty(failed);
        Assert.True(fx.Coordinator.UpdateInProgress, "run 2 không được bị reset bởi Fail của run 1");
        Assert.False(fx.Stopper.StopCalled);

        fx.Updates.DownloadTcs.SetResult(Path.Combine(Path.GetTempPath(), "retran-9.9.9.zip"));
        await startRun2;

        Assert.Equal(["download", "download", "verify", "stop", "install", "quit"], fx.Calls);
        Assert.Empty(failed);
    }

    // ===== Test doubles =====

    private sealed record Fixtures(
        UpdateCoordinator Coordinator,
        FakeUpdateService Updates,
        FakeStopper Stopper,
        FakeShutdown Shutdown,
        FakeRecovery Recovery,
        RecordingLogger<UpdateCoordinator> Log,
        List<string> Calls) : IDisposable
    {
        public void Dispose() => Coordinator.Dispose();
    }

    /// <summary>Fake IAppUpdateService — download kiểm soát bằng TCS (đổi được giữa các run), verify/install trả cờ set được.</summary>
    private sealed class FakeUpdateService(List<string> calls) : IAppUpdateService
    {
        public TaskCompletionSource<string?> DownloadTcs { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool VerifyResult { get; set; } = true;

        public bool InstallResult { get; set; } = true;

        public Exception? ThrowOnInstall { get; set; }

        public string CurrentVersion => "9.9.8.0";

        public string? DownloadedZipPath { get; set; }

        public CancellationToken DownloadCancellationToken { get; private set; }

        public Task<ReleaseInfo?> CheckForUpdatesAsync() => Task.FromResult<ReleaseInfo?>(null);

        public bool IsUpdateAvailable(ReleaseInfo release) => false;

        public Task<string?> DownloadUpdateAsync(
            ReleaseInfo release, IProgress<int>? progress, CancellationToken cancellationToken = default)
        {
            DownloadCancellationToken = cancellationToken;
            calls.Add("download");
            return DownloadTcs.Task;
        }

        public bool VerifyDownloadedPackage()
        {
            calls.Add("verify");
            return VerifyResult;
        }

        public Task<bool> InstallUpdateAsync()
        {
            calls.Add("install");
            if (ThrowOnInstall is not null)
            {
                throw ThrowOnInstall;
            }

            return Task.FromResult(InstallResult);
        }
    }

    private sealed class CallbackException(Func<string> message) : Exception
    {
        public override string Message => message();
    }

    /// <summary>Fake IUpdateStopper — ghi "stop", ném ThrowOnStop, BlockStop giữ pipeline trong window finalise.</summary>
    private sealed class FakeStopper(List<string> calls) : IUpdateStopper
    {
        private int _stopCalled;

        public bool StopCalled => Volatile.Read(ref _stopCalled) != 0;

        public Exception? ThrowOnStop { get; set; }

        /// <summary>Chặn StopServicesAsync cho tới khi test giải phóng — giữ pipeline trong trạng thái finalise.</summary>
        public TaskCompletionSource<bool>? StopBlock { get; private set; }

        /// <summary>Chặn stop cho tới khi test giải phóng — trả về "cửa" để test mở lại.</summary>
        public TaskCompletionSource<bool> BlockStop()
        {
            StopBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return StopBlock;
        }

        public async Task StopServicesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _stopCalled);
            calls.Add("stop");
            if (ThrowOnStop is not null)
            {
                throw ThrowOnStop;
            }

            // Quan sát token: Dispose/timeout huỷ trong lúc chặn → ném OCE (cooperative stopper)
            if (StopBlock is not null)
            {
                await StopBlock.Task.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private sealed class FakeRecovery(List<string> calls) : IAppRecovery
    {
        public bool ThrowOnRestart { get; set; }

        public void RestartCurrentApp()
        {
            calls.Add("recover");
            if (ThrowOnRestart)
            {
                throw new InvalidOperationException("simulated current-app restart failure");
            }
        }
    }

    /// <summary>Fake IAppShutdown — ghi "quit" + số lần gọi, ThrowOnQuit mô phỏng app không đóng được.</summary>
    private sealed class FakeShutdown(List<string> calls) : IAppShutdown
    {
        private int _quitCalled;

        public int QuitCalled => Volatile.Read(ref _quitCalled);

        public bool ThrowOnQuit { get; set; }

        public void Quit()
        {
            calls.Add("quit");
            Interlocked.Increment(ref _quitCalled);
            if (ThrowOnQuit)
            {
                throw new InvalidOperationException("simulated quit failure");
            }
        }
    }

    /// <summary>ILogger ghi lại message ≥ Warning — test case 10 kiểm chứng log warn khi Quit ném.</summary>
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
