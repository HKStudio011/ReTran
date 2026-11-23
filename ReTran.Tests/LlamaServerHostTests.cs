using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ReTran.Core.Llm;

namespace RetranCore.Tests;

public class LlamaServerHostTests
{
    [Fact]
    public void BuildArguments_ContainsModelPortAndFlags()
    {
        string args = LlamaServerHost.BuildArguments(@"C:\models\m.gguf", 18080, 4096, 99);

        Assert.Contains("-m \"C:\\models\\m.gguf\"", args);
        Assert.Contains("--host 127.0.0.1", args);
        Assert.Contains("--port 18080", args);
        Assert.Contains("-c 4096", args);
        Assert.Contains("-ngl 99", args);
        Assert.Contains("--jinja", args);
    }

    [Fact]
    public void PickFreePort_IsBindable()
    {
        int port = LlamaServerHost.PickFreePort();

        Assert.InRange(port, 1024, 65535);
        // The port must still be bindable immediately after being returned.
        // Cổng phải còn bind được ngay sau khi trả về.
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }

    [Fact]
    public async Task WaitForHealth_TrueWhenProbeSucceeds()
    {
        int calls = 0;

        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ => { calls++; return Task.FromResult(calls >= 2); },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.True(calls >= 2);
    }

    [Fact]
    public async Task WaitForHealth_TimesOut()
    {
        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ => Task.FromResult(false),
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(20),
            TestContext.Current.CancellationToken);

        Assert.False(ok);
    }

    [Fact]
    public async Task WaitForHealth_ProbeThrows_KeepsTrying()
    {
        int calls = 0;

        bool ok = await LlamaServerHost.WaitForHealthAsync(
            _ =>
            {
                calls++;
                if (calls < 3) throw new HttpRequestException("connection refused");
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(10),
            TestContext.Current.CancellationToken);

        Assert.True(ok);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("ram")]
    [InlineData("faults")]
    [InlineData("cpu")]
    [InlineData("none")]
    [InlineData("unreadable")]
    public async Task HealthWait_ActivityExtends_StallFails_UnreadableCaps(string signal)
    {
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        bool readable = signal != "unreadable";
        var metrics = new ScriptedMetrics(read => !readable
            ? null
            : SampleFor(signal, read));
        using LlamaServerHost host = CreateHost(dir, metrics, clock, LongRunningCommand);
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);

            if (signal is "io" or "ram" or "faults" or "cpu")
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                await metrics.WaitForReadAsync(2);
                clock.Advance(TimeSpan.FromSeconds(595));
                await DrainAsync();
                Assert.False(startup.IsCompleted); // activity at 5 s moved the deadline to 605 s
                clock.Advance(TimeSpan.FromSeconds(5));
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(599));
                await DrainAsync();
                Assert.False(startup.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1));
            }

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Contains("no loading activity for 600 s", error.Message, StringComparison.Ordinal);
            Assert.Contains("không có hoạt động trong 10 phút", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task HealthWait_LateHealthSuccess_AfterFixedCeiling_IsRejected()
    {
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(_ => null);
        using LlamaServerHost host = CreateHost(
            dir, metrics, clock, LongRunningCommand,
            (_, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(601));
                return Task.FromResult(true);
            });
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Contains("no loading activity for 600 s", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task HealthWait_FinalWindowActivity_RenewsInsteadOfKilling()
    {
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(read => SampleFor("io", read));
        using LlamaServerHost host = CreateHost(dir, metrics, clock, LongRunningCommand);
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);
            clock.Advance(TimeSpan.FromSeconds(600));
            await DrainAsync();

            Assert.False(startup.IsCompleted); // IO in the 595–600 sample must renew before the stall check
            await metrics.WaitForReadAsync(2);
            clock.Advance(TimeSpan.FromSeconds(599));
            await DrainAsync();
            Assert.False(startup.IsCompleted);

            clock.Advance(TimeSpan.FromSeconds(1));
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.Contains("no loading activity for 600 s", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Progress_MonotonicClamped_UntilHealthy()
    {
        const long MiB = 1024 * 1024;
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(read => new LlamaProcessSample(
            TimeSpan.Zero,
            WorkingSetBytes: 4 * MiB,
            ReadBytes: read switch { 0 => 2 * MiB, 1 => MiB, _ => 16 * MiB },
            PageFaults: 0));
        bool healthy = false;
        using LlamaServerHost host = CreateHost(
            dir, metrics, clock, LongRunningCommand, (_, _) => Task.FromResult(healthy));
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);
            Assert.Equal(25, host.LoadProgress.Percent);

            clock.Advance(TimeSpan.FromSeconds(5));
            await metrics.WaitForReadAsync(2);
            Assert.Equal(2 * MiB, host.LoadProgress.BytesLoaded); // IO regression cannot move percent backwards
            Assert.False(host.LoadProgress.ProgressAvailable); // a flat/regressed high-water is indeterminate
            Assert.Null(host.LoadProgress.BytesPerSec);
            Assert.Null(host.LoadProgress.Percent);

            clock.Advance(TimeSpan.FromSeconds(5));
            await metrics.WaitForReadAsync(3);
            Assert.Equal(16 * MiB, host.LoadProgress.BytesLoaded);
            Assert.Equal(99, host.LoadProgress.Percent);
            Assert.Equal("finalizing", host.LoadProgress.Phase);
            Assert.True(host.LoadProgress.ProgressAvailable);

            healthy = true;
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal("ready", host.LoadProgress.Phase);
            Assert.Equal(100, host.LoadProgress.Percent);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Progress_RequiresFivePercentAndIncreasingHighWater()
    {
        const long threshold = (TestModelBytes + 19) / 20; // ceiling of exactly five percent
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(read => new LlamaProcessSample(
            TimeSpan.Zero,
            WorkingSetBytes: 0,
            ReadBytes: read switch
            {
                0 => threshold - 1,
                1 => threshold,
                2 => threshold,
                _ => threshold + 1,
            },
            PageFaults: 0));
        using LlamaServerHost host = CreateHost(dir, metrics, clock, LongRunningCommand);
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);
            Assert.False(host.LoadProgress.ProgressAvailable);
            Assert.Null(host.LoadProgress.BytesPerSec);
            Assert.Null(host.LoadProgress.Percent);

            clock.Advance(TimeSpan.FromSeconds(5));
            await metrics.WaitForReadAsync(2);
            Assert.True(host.LoadProgress.ProgressAvailable); // exactly 5%, and the high-water increased
            Assert.NotNull(host.LoadProgress.BytesPerSec);
            Assert.NotNull(host.LoadProgress.Percent);

            clock.Advance(TimeSpan.FromSeconds(5));
            await metrics.WaitForReadAsync(3);
            Assert.False(host.LoadProgress.ProgressAvailable); // high-water stopped increasing
            Assert.Null(host.LoadProgress.BytesPerSec); // no ETA input while indeterminate
            Assert.Null(host.LoadProgress.Percent);

            clock.Advance(TimeSpan.FromSeconds(5));
            await metrics.WaitForReadAsync(4);
            Assert.True(host.LoadProgress.ProgressAvailable);
            Assert.NotNull(host.LoadProgress.BytesPerSec);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Progress_MissingCounters_Indeterminate()
    {
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(_ => null);
        using LlamaServerHost host = CreateHost(dir, metrics, clock, LongRunningCommand);
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);
            Assert.False(host.LoadProgress.ProgressAvailable);
            Assert.Null(host.LoadProgress.BytesLoaded);
            Assert.Null(host.LoadProgress.BytesPerSec);
            Assert.Null(host.LoadProgress.Percent);
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task Dispose_CancelsHealthWaitPromptly()
    {
        string dir = NewTempDir();
        var clock = new FakeTimeProvider();
        var metrics = new ScriptedMetrics(_ => new LlamaProcessSample(TimeSpan.Zero, 0, 0, 0));
        var host = CreateHost(dir, metrics, clock, LongRunningCommand);
        Task startup = host.EnsureStartedAsync(TestContext.Current.CancellationToken);
        try
        {
            await metrics.WaitForReadAsync(1);
            host.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => startup.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            host.Dispose();
            await ObserveCancellationAsync(startup);
            DeleteTempDir(dir);
        }
    }

    [Fact]
    public async Task HealthWait_ProcessExit_ReturnsStderrTail()
    {
        string dir = NewTempDir();
        var metrics = new ScriptedMetrics(_ => null);
        using LlamaServerHost host = CreateHost(
            dir, metrics, TimeProvider.System,
            "/d /c \"echo fatal-load-error 1>&2 & exit 7\"");
        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => host.EnsureStartedAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            Assert.Contains("exited before becoming healthy", error.Message, StringComparison.Ordinal);
            Assert.Contains("stderr tail", error.Message, StringComparison.Ordinal);
            Assert.Contains("fatal-load-error", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempDir(dir);
        }
    }

    private const string LongRunningCommand = "/d /c \"ping -t 127.0.0.1 >nul\"";
    private const long TestModelBytes = 8 * 1024 * 1024;

    private static LlamaProcessSample SampleFor(string signal, int read)
    {
        const long MiB = 1024 * 1024;
        if (read != 1) return new LlamaProcessSample(TimeSpan.Zero, 0, 0, 0);
        return signal switch
        {
            "io" => new LlamaProcessSample(TimeSpan.Zero, 0, MiB, 0),
            "ram" => new LlamaProcessSample(TimeSpan.Zero, 2 * MiB, 0, 0),
            "faults" => new LlamaProcessSample(TimeSpan.Zero, 0, 0, 64),
            "cpu" => new LlamaProcessSample(TimeSpan.FromMilliseconds(300), 0, 0, 0),
            _ => new LlamaProcessSample(TimeSpan.Zero, 0, 0, 0),
        };
    }

    private static LlamaServerHost CreateHost(
        string dir,
        ILlamaProcessMetrics metrics,
        TimeProvider clock,
        string command,
        Func<int, CancellationToken, Task<bool>>? healthProbe = null)
    {
        string server = Path.Combine(dir, "server.exe");
        string model = Path.Combine(dir, "model.gguf");
        File.WriteAllBytes(server, [0]);
        using (FileStream stream = File.Create(model)) stream.SetLength(TestModelBytes);
        return new LlamaServerHost(
            server, model, 4096, 99, metrics, clock,
            _ => Process.Start(new ProcessStartInfo("cmd.exe", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }),
            healthProbe ?? ((_, _) => Task.FromResult(false)));
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"retran-llama-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTempDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
    }

    private static async Task DrainAsync()
    {
        for (int i = 0; i < 20; i++) await Task.Yield();
    }

    private sealed class ScriptedMetrics(Func<int, LlamaProcessSample?> sampleFactory) : ILlamaProcessMetrics
    {
        private int _reads;
        private TaskCompletionSource _changed = NewSignal();

        public bool TryRead(Process process, out LlamaProcessSample sample)
        {
            int read = Interlocked.Increment(ref _reads) - 1;
            LlamaProcessSample? next = sampleFactory(read);
            sample = next ?? default!;
            TaskCompletionSource signal;
            lock (this)
            {
                signal = _changed;
                _changed = NewSignal();
            }
            signal.TrySetResult();
            return next is not null;
        }

        public Task WaitForReadAsync(int count)
        {
            while (Volatile.Read(ref _reads) < count)
            {
                TaskCompletionSource signal;
                lock (this) signal = _changed;
                if (Volatile.Read(ref _reads) >= count) break;
                return AwaitReadAsync(signal.Task, count);
            }
            return Task.CompletedTask;
        }

        private async Task AwaitReadAsync(Task signal, int count)
        {
            await signal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await WaitForReadAsync(count).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _now;
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new FakeTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = _timers.Where(timer => timer.DueAt is not null && timer.DueAt <= _now).ToList();
                foreach (FakeTimer timer in due) timer.ScheduleNext(_now);
            }
            foreach (FakeTimer timer in due) timer.Tick();
        }

        private sealed class FakeTimer : ITimer
        {
            private readonly FakeTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private TimeSpan _period;
            private bool _disposed;

            internal FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
            }

            internal DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._gate)
                {
                    if (_disposed) return false;
                    _period = period;
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
                    return true;
                }
            }

            internal void ScheduleNext(DateTimeOffset now)
            {
                DueAt = _period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan ? now + _period : null;
            }

            internal void Tick()
            {
                if (!_disposed) _callback(_state);
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    _disposed = true;
                    DueAt = null;
                    _owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
