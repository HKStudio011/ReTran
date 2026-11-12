using System.Text.Json.Nodes;
using ReTran.Core.Capture;
using ReTran.Core.Cli;
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
    public void TryParse_MergeFlags_SetsThresholds()
    {
        var options = OcrLoop.TryParse(
            ["--monitor", "0", "--merge-lines", "--merge-gap", "0.25", "--merge-height", "2", "--merge-overlap", "0"],
            3, out string? error);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(new MergeThresholds(0.25, 2.0, 0.0), options!.Merge);
    }

    [Fact]
    public void TryParse_MergeFlagsAbsent_DefaultThresholds()
    {
        // A missing flag set must land on MergeThresholds.Default, never on the record struct's
        // zero value (0/0/0), which is out of range and would change the merge behaviour.
        // Thiếu bộ cờ phải ra MergeThresholds.Default, không phải giá trị 0 của record struct
        // (0/0/0) — ngoài dải và làm thay đổi hành vi gộp dòng.
        var options = OcrLoop.TryParse(["--monitor", "0"], 3, out string? error);
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal(MergeThresholds.Default, options!.Merge);
    }

    [Fact]
    public void TryParse_MergeFlagMissingValue_Errors()
    {
        (string[] Args, string Flag)[] cases =
        [
            (["--merge-gap"], "--merge-gap"),
            (["--monitor", "0", "--merge-height"], "--merge-height"),
            (["--monitor", "0", "--merge-overlap"], "--merge-overlap"),
        ];
        foreach ((string[] args, string flag) in cases)
        {
            Assert.Null(OcrLoop.TryParse(args, 3, out string? error));
            Assert.Contains(flag, error, StringComparison.Ordinal);
            Assert.Contains("missing value", error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TryParse_MergeFlagOutOfRange_Errors()
    {
        (string[] Args, string Flag)[] cases =
        [
            (["--monitor", "0", "--merge-gap", "2.5"], "--merge-gap"),       // above GapRatioHigh
            (["--monitor", "0", "--merge-gap", "0.05"], "--merge-gap"),      // below GapRatioLow
            (["--monitor", "0", "--merge-height", "3.5"], "--merge-height"), // above HeightRatioHigh
            (["--monitor", "0", "--merge-overlap", "1.5"], "--merge-overlap"), // above OverlapRatioHigh
            (["--monitor", "0", "--merge-gap", "abc"], "--merge-gap"),       // not a number
            (["--monitor", "0", "--merge-gap", "0,6"], "--merge-gap"),       // vi-VN comma decimal (R12)
        ];
        foreach ((string[] args, string flag) in cases)
        {
            Assert.Null(OcrLoop.TryParse(args, 3, out string? error));
            Assert.Contains(flag, error, StringComparison.Ordinal);
            Assert.Contains("invalid", error, StringComparison.OrdinalIgnoreCase);
        }

        // The range bounds are inclusive, and the message quotes them from MergeThresholds'
        // consts in InvariantCulture ("2.0", never "2").
        // Biên dải được bao gồm, và message lấy dải từ hằng số của MergeThresholds theo
        // InvariantCulture ("2.0", không bao giờ "2").
        Assert.NotNull(OcrLoop.TryParse(["--monitor", "0", "--merge-gap", "2.0", "--merge-overlap", "0"], 3, out string? edgeError));
        Assert.Null(edgeError);
        Assert.Null(OcrLoop.TryParse(["--monitor", "0", "--merge-gap", "2.5"], 3, out string? rangeError));
        Assert.Contains("2.0", rangeError, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_MentionsEveryMergeFlag()
    {
        string[] flags = ["--merge-gap", "--merge-height", "--merge-overlap"];
        foreach (string flag in flags)
            Assert.Contains(flag, OcrLoop.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParseOneShot_MergeLinesFlag_SetsOption()
    {
        Assert.True(OcrCommands.TryParseOneShot(["a.png", "--merge-lines"],
            out string? input, out bool mergeLines, out MergeThresholds merge, out string? error));
        Assert.Null(error);
        Assert.Equal("a.png", input);
        Assert.True(mergeLines);
        Assert.Equal(MergeThresholds.Default, merge); // no threshold flags -> defaults

        Assert.True(OcrCommands.TryParseOneShot(["a.png"], out input, out mergeLines, out merge, out _));
        Assert.Equal("a.png", input);
        Assert.False(mergeLines); // default off
        Assert.Equal(MergeThresholds.Default, merge);
    }

    [Fact]
    public void TryParseOneShot_MergeFlags_SetsThresholdsOrErrors()
    {
        Assert.True(OcrCommands.TryParseOneShot(["a.png", "--merge-lines", "--merge-gap", "0.3"],
            out string? input, out bool mergeLines, out MergeThresholds merge, out string? error));
        Assert.Null(error);
        Assert.Equal("a.png", input);
        Assert.True(mergeLines);
        Assert.Equal(0.3, merge.GapRatio);
        Assert.Equal(MergeThresholds.Default.HeightRatio, merge.HeightRatio); // untouched flag keeps its default

        Assert.False(OcrCommands.TryParseOneShot(["a.png", "--merge-gap"], out _, out _, out _, out string? missing));
        Assert.Contains("--merge-gap", missing, StringComparison.Ordinal);

        Assert.False(OcrCommands.TryParseOneShot(["a.png", "--merge-height", "0.5"], out _, out _, out _, out string? range));
        Assert.Contains("--merge-height", range, StringComparison.Ordinal);
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
            Assert.False(OcrCommands.TryParseOneShot(args, out _, out _, out _, out string? error));
            Assert.False(string.IsNullOrEmpty(error), $"no error for: [{string.Join(' ', args)}]");
        }
    }

    [Fact]
    public async Task Ocr_Loop_MergeFlagOutOfRange_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["ocr", "loop", "--monitor", "0", "--merge-gap", "2.5"]);
        Assert.Equal(2, exit);
        Assert.Contains("--merge-gap", stderr, StringComparison.Ordinal);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[--merge-gap N]", stderr, StringComparison.Ordinal); // every flag is documented
    }

    [Fact]
    public async Task Ocr_Loop_MergeFlagMissingValue_UsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["ocr", "loop", "--monitor", "0", "--merge-overlap"]);
        Assert.Equal(2, exit);
        Assert.Contains("--merge-overlap", stderr, StringComparison.Ordinal);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ocr_OneShot_MergeFlagMissingValue_UsageExit2()
    {
        // Parse failure exits 2 before the sidecar is spawned, so this stays environment-free.
        // Lỗi parse thoát 2 trước khi spawn sidecar, nên test không phụ thuộc môi trường.
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["ocr", "a.png", "--merge-gap"]);
        Assert.Equal(2, exit);
        Assert.Contains("--merge-gap", stderr, StringComparison.Ordinal);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[--merge-gap N] [--merge-height N] [--merge-overlap N]", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OcrLoop_RealSidecar_FakeSource_EmitsTwoJsonLines()
    {
        // Infra gate: repo+venv (resolved from AppContext.BaseDirectory, see TryResolveRepoWithVenv) +
        // paddle warm-up — otherwise report a skip with a reason (never fail on environment, never a
        // silent return that xUnit counts as passed with zero sidecar coverage).
        if (!OperatingSystem.IsWindows())
            Assert.Skip("real-sidecar E2E is Windows-only (venv layout + sidecar spawn)");
        if (!OcrTestSupport.TryResolveRepoWithVenv(out string repoRoot, out string python))
            Assert.Skip("no ancestor of AppContext.BaseDirectory has both ReTran.OCR/pyproject.toml and ReTran.OCR/.venv/Scripts/python.exe (repo layout or uv venv missing)");

        string fixture = OcrTestSupport.CreateTextFixturePng();
        try
        {
            if (!await OcrTestSupport.TryWarmUpModel(python, repoRoot, fixture))
                Assert.Skip("PaddleOCR import or model warm-up failed (paddle/model download unavailable or broken venv)");

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
