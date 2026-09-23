using System.Text.Json.Nodes;
using ReTran.Core.Cli;
using ReTran.Core.Config;
using ReTran.Core.Ocr;
using Xunit;

namespace RetranCore.Tests;

public class OcrCliTests
{
    [Fact]
    public async Task Ocr_WithoutArgs_ReturnsUsageExit2()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["ocr"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ocr_MissingFile_ReturnsExit1_BeforeAnySidecar()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        var (_, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d,
            ["ocr", Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.png")]);
        Assert.Equal(1, exit);
        Assert.Contains("not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindRepoRoot_FindsDirectoryContainingOcrProject()
    {
        string? root = OcrBootstrap.FindRepoRoot();
        if (root is null) return; // running outside repo layout -> skip
        Assert.True(File.Exists(Path.Combine(root, "ReTran.OCR", "pyproject.toml")));
    }

    [Fact]
    public async Task Ocr_RealSidecar_EndToEnd_PrintsSpotsJson()
    {
        // Infra gate: repo + venv + paddle import + model warm-up — otherwise skip (never fail on environment).
        if (!OperatingSystem.IsWindows()) return;
        string? repoRoot = OcrBootstrap.FindRepoRoot();
        if (repoRoot is null) return;
        string python = OcrBootstrap.ResolvePythonExe(new CoreConfig(), repoRoot) ?? "";
        if (python.Length == 0 || !File.Exists(python)) return;

        string fixture = OcrTestSupport.CreateTextFixturePng();
        try
        {
            if (!await OcrTestSupport.TryWarmUpModel(python, repoRoot, fixture)) return; // no paddle/model/network -> skip

            var d = new CommandDispatcher();
            OcrCommands.Register(d);
            var (stdout, stderr, exit) = await OcrTestSupport.RunDispatcherAsync(d, ["ocr", fixture]);
            Assert.True(exit == 0, $"ocr CLI failed (exit {exit}): {stderr}");
            var json = JsonNode.Parse(stdout)!;
            Assert.NotEmpty(json["spots"]!.AsArray());
            Assert.Contains("HELLO", string.Join(" ", json["spots"]!.AsArray()
                .Select(s => s!["text"]!.GetValue<string>())).ToUpperInvariant());
            Assert.NotNull(json["new_texts"]);
        }
        finally { File.Delete(fixture); }
    }
}
