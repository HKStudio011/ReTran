using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class OcrLoopCliTests
{
    [Fact]
    public async Task Ocr_Loop_BareArgs_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (stdout, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["ocr", "loop"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", stdout);
    }

    [Fact]
    public async Task Ocr_Loop_BothSources_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["ocr", "loop", "--monitor", "0", "--hwnd", "0x1234"]);
        Assert.Equal(2, exit);
        Assert.Contains("exactly one source", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ocr_Loop_InvalidArgs_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        string[][] cases =
        [
            ["ocr", "loop", "--monitor", "abc"],   // bad index
            ["ocr", "loop", "--fps", "0"],         // fps out of range
            ["ocr", "loop", "--monitor", "99"],    // monitor out of range (CreateSource, still pre-sidecar)
            ["ocr", "loop", "--bogus"],
        ];
        foreach (string[] args in cases)
        {
            var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, args);
            Assert.True(exit == 2, $"expected exit 2 for [{string.Join(' ', args)}], got {exit}: {stderr}");
        }
    }

    [Fact]
    public void TryParse_MergeLinesFlag_SetsOption()
    {
        var with = OcrLoop.TryParse(["--monitor", "0", "--merge-lines"], 3, out string? errWith);
        Assert.Null(errWith);
        Assert.NotNull(with);
        Assert.True(with!.MergeLines);

        var without = OcrLoop.TryParse(["--monitor", "0"], 3, out _);
        Assert.NotNull(without);
        Assert.False(without!.MergeLines); // default off
    }

    [Fact]
    public void TryParseOneShot_MergeLinesFlag_SetsOption()
    {
        Assert.True(OcrCommands.TryParseOneShot(["a.png", "--merge-lines"], out string? input, out bool mergeLines, out string? error));
        Assert.Null(error);
        Assert.Equal("a.png", input);
        Assert.True(mergeLines);

        Assert.True(OcrCommands.TryParseOneShot(["a.png"], out input, out mergeLines, out _));
        Assert.Equal("a.png", input);
        Assert.False(mergeLines); // default off
    }

    [Fact]
    public void TryParseOneShot_InvalidArgs_ReturnsError()
    {
        string[][] cases =
        [
            [],                        // no input
            ["a.png", "b.png"],        // two positionals
            ["--bogus"],               // unknown flag
        ];
        foreach (string[] args in cases)
        {
            Assert.False(OcrCommands.TryParseOneShot(args, out _, out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error), $"no error for: [{string.Join(' ', args)}]");
        }
    }

    [Fact]
    public async Task OcrLoop_RealSidecar_FakeSource_EmitsTwoJsonLines()
    {
        // Infra gate: repo + venv + paddle warm-up — otherwise skip (never fail on environment).
        if (!OperatingSystem.IsWindows()) return;
        string? repoRoot = OcrBootstrap.FindRepoRoot();
        if (repoRoot is null) return;
        string python = OcrBootstrap.ResolvePythonExe(new CoreConfig(), repoRoot) ?? "";
        if (python.Length == 0 || !File.Exists(python)) return;

        string fixture = OcrTestSupport.CreateTextFixturePng();
        try
        {
            if (!await OcrTestSupport.TryWarmUpModel(python, repoRoot, fixture)) return;

            await using var sp = await SidecarProcess.StartAsync(python, repoRoot, CancellationToken.None);
            var pipeline = new OcrPipeline(sp, new OcrDiff());
            var options = new OcrLoopOptions(false, 0, 0, Fps: 5, MaxFrames: 2);
            using var source = new FakeCaptureSource();
            var stdout = new SyncTextWriter();
            var stderr = new StringWriter();

            Task<int> run = Task.Run(() =>
                OcrLoop.RunAsync(options, source, pipeline, new JsonLinesSink(stdout), stderr, CancellationToken.None));

            using var feederCts = new CancellationTokenSource();
            Task feeder = Task.Run(async () =>
            {
                while (!feederCts.IsCancellationRequested && !run.IsCompleted)
                {
                    source.Raise(OcrTestSupport.FrameFromPng(fixture));
                    try { await Task.Delay(100, feederCts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }, TestContext.Current.CancellationToken);

            Assert.True(await stdout.WaitForLineCount(2, TimeSpan.FromSeconds(60)), "no 2 lines");
            feederCts.Cancel();
            int exit = await run;
            Assert.True(exit == 0, $"loop failed (exit {exit}): {stderr}");

            string[] lines = stdout.Snapshot();
            Assert.Equal(2, lines.Length);
            JsonObject first = JsonNode.Parse(lines[0])!.AsObject();
            JsonObject second = JsonNode.Parse(lines[1])!.AsObject();
            Assert.Equal(1, first["seq"]!.GetValue<long>());
            Assert.Equal(2, second["seq"]!.GetValue<long>());
            Assert.NotNull(first["new_texts"] as JsonArray);
            Assert.NotNull(second["at"]!.GetValue<string>());
        }
        finally { File.Delete(fixture); }
    }
}
