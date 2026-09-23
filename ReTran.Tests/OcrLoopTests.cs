using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;
using Frame = ReTran.Core.Capture.Frame;

namespace RetranCore.Tests;

public class OcrLoopTests
{
    private sealed class FakeRpc : ISidecarRpc
    {
        private readonly Func<int, CancellationToken, Task<JsonNode?>> _onCall;
        private int _calls;

        public FakeRpc(Func<int, CancellationToken, Task<JsonNode?>>? onCall = null)
            => _onCall = onCall ?? ((_, _) => Task.FromResult<JsonNode?>(SpotResult("HI")));

        public Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
        {
            int call = Interlocked.Increment(ref _calls);
            return _onCall(call, ct);
        }
    }

    private static JsonObject SpotResult(params string[] texts)
    {
        var spotArray = new JsonArray(texts
            .Select(t => (JsonNode)new JsonObject
            {
                ["box"] = new JsonArray(
                    new JsonArray(0, 0), new JsonArray(1, 0),
                    new JsonArray(1, 1), new JsonArray(0, 1)),
                ["text"] = t,
                ["score"] = 0.9,
            })
            .ToArray());
        return new JsonObject
        {
            ["spots"] = spotArray,
            ["elapsed_ms"] = 5,
            ["new_texts"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
        };
    }

    private static Frame TinyFrame() => Frame.FromBgra(2, 2, new byte[16]);

    [Fact]
    public void TryParse_ValidArgs_ReturnsOptions()
    {
        var a = OcrLoop.TryParse(["--monitor", "1", "--fps", "5", "--max-frames", "7"], 3, out string? errA);
        Assert.Null(errA);
        Assert.NotNull(a);
        Assert.False(a.UseWindow);
        Assert.Equal(1, a.Monitor);
        Assert.Equal(5, a.Fps);
        Assert.Equal(7, a.MaxFrames);

        var b = OcrLoop.TryParse(["--hwnd", "0x1F0"], 3, out _);
        Assert.NotNull(b);
        Assert.True(b.UseWindow);
        Assert.Equal((nint)0x1F0, b.Hwnd);

        var c = OcrLoop.TryParse(["--monitor", "0"], 9, out _); // defaultFps used when --fps absent
        Assert.NotNull(c);
        Assert.Equal(9, c.Fps);
    }

    [Fact]
    public void TryParse_InvalidArgs_ReturnsNullWithError()
    {
        string[][] cases =
        [
            [],
            ["--monitor", "0", "--hwnd", "0x10"],   // both sources
            ["--monitor", "abc"],
            ["--monitor", "-1"],
            ["--hwnd"],                              // missing value
            ["--fps", "0"],
            ["--fps", "61"],
            ["--max-frames", "-1"],
            ["--bogus"],
        ];
        foreach (string[] args in cases)
        {
            Assert.Null(OcrLoop.TryParse(args, 3, out string? error));
            Assert.False(string.IsNullOrEmpty(error), $"no error for: [{string.Join(' ', args)}]");
        }
    }

    [Fact]
    public async Task RunAsync_MaxFrames_EmitsThatManyLines_ThenReturns0()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 2);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame()); // delivered on Start; consumed by the first-frame wait
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400); // first-frame wait polls every 50 ms — A consumed by now
        source.Raise(TinyFrame());
        Assert.True(await stdout.WaitForLineCount(1, TimeSpan.FromSeconds(10)), "no line 1");
        source.Raise(TinyFrame());
        Assert.True(await stdout.WaitForLineCount(2, TimeSpan.FromSeconds(10)), "no line 2");

        Assert.Equal(0, await run);
        string[] lines = stdout.Snapshot();
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonNode.Parse(lines[0])!["seq"]!.GetValue<long>());
        Assert.Equal(2, JsonNode.Parse(lines[1])!["seq"]!.GetValue<long>());
        JsonObject parsed = JsonNode.Parse(lines[1])!.AsObject();
        Assert.NotNull(parsed["at"]);
        Assert.NotNull(parsed["new_texts"]);
        Assert.Equal(1, parsed["spots_count"]!.GetValue<int>());
        Assert.Equal(5, parsed["elapsed_ms"]!.GetValue<int>());
    }

    [Fact]
    public async Task RunAsync_NoNewFrame_CancelsCleanlyWithoutEmit()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 0);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();
        using var cts = new CancellationTokenSource();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, cts.Token));

        await Task.Delay(1500); // first frame consumed, >=1 tick elapsed with an empty buffer
        Assert.Empty(stdout.Snapshot()); // skipped ticks emit nothing
        cts.Cancel();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task RunAsync_FirstFrameTimeout_Returns1()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 60, MaxFrames: 1);
        using var source = new FakeCaptureSource(); // never raises
        var pipeline = new OcrPipeline(new FakeRpc(), new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        int exit = await OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("timed out", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stdout.Snapshot());
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task RunAsync_TimeoutCycle_WarnsAndContinues()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 1);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var rpc = new FakeRpc((call, ct) =>
            call == 1
                ? Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => (JsonNode?)null,
                    TaskContinuationOptions.ExecuteSynchronously)
                : Task.FromResult<JsonNode?>(SpotResult("HI")));
        var pipeline = new OcrPipeline(rpc, new OcrDiff()) { Timeout = TimeSpan.FromMilliseconds(300) };
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400);   // first frame (A) consumed
        source.Raise(TinyFrame()); // cycle 1 -> times out at ~1.3 s -> warn + skip
        await Task.Delay(1500);
        source.Raise(TinyFrame()); // cycle 2 -> fast -> emits
        Assert.True(await stdout.WaitForLineCount(1, TimeSpan.FromSeconds(10)), "no line after timeout cycle");

        Assert.Equal(0, await run);
        Assert.Contains("cycle skipped", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(stdout.Snapshot());
    }

    [Fact]
    public async Task RunAsync_SidecarDead_Returns1()
    {
        var options = new OcrLoopOptions(false, 0, 0, Fps: 1, MaxFrames: 0);
        using var source = new FakeCaptureSource();
        source.Raise(TinyFrame());
        var rpc = new FakeRpc((_, _) =>
            throw new SidecarRpcException(-32603, "sidecar exited"));
        var pipeline = new OcrPipeline(rpc, new OcrDiff());
        var stdout = new SyncTextWriter();
        var stderr = new StringWriter();

        Task<int> run = Task.Run(() =>
            OcrLoop.RunAsync(options, source, pipeline, stdout, stderr, CancellationToken.None));

        await Task.Delay(400);
        source.Raise(TinyFrame()); // processed on the first tick -> fatal

        Assert.Equal(1, await run);
        Assert.Contains("sidecar lost", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stdout.Snapshot());
        Assert.False(source.IsRunning);
    }
}
