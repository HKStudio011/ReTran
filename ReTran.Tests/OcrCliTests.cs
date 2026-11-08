using System.Text.Json.Nodes;
using ReTran.Core.Cli;
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
        if (root is null)
            Assert.Skip("no ancestor of the test host CWD contains ReTran.OCR/pyproject.toml (running outside the repo layout)");
        Assert.True(File.Exists(Path.Combine(root, "ReTran.OCR", "pyproject.toml")));
    }

    [Fact]
    public async Task Ocr_RealSidecar_EndToEnd_PrintsSpotsJson()
    {
        // Infra gate: repo+venv (resolved from AppContext.BaseDirectory, see TryResolveRepoWithVenv) +
        // paddle import + model warm-up — otherwise report a skip with a reason (never fail on environment,
        // never a silent return that xUnit counts as passed with zero sidecar coverage).
        if (!OperatingSystem.IsWindows())
            Assert.Skip("real-sidecar E2E is Windows-only (venv layout + sidecar spawn)");
        if (!OcrTestSupport.TryResolveRepoWithVenv(out string repoRoot, out string python))
            Assert.Skip("no ancestor of AppContext.BaseDirectory has both ReTran.OCR/pyproject.toml and ReTran.OCR/.venv/Scripts/python.exe (repo layout or uv venv missing)");

        string fixture = OcrTestSupport.CreateTextFixturePng();
        try
        {
            if (!await OcrTestSupport.TryWarmUpModel(python, repoRoot, fixture))
                Assert.Skip("PaddleOCR import or model warm-up failed (paddle/model download unavailable or broken venv)");

            var d = new CommandDispatcher();
            OcrCommands.Register(d);
            // The ocr CLI resolves its repo/venv from the process CWD (production contract: Core runs with
            // CWD = app dir), so run it with the CWD pinned to the repo root we resolved above.
            var (stdout, stderr, exit) = await OcrTestSupport.RunDispatcherAtRepoRootAsync(d, ["ocr", fixture], repoRoot);
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
