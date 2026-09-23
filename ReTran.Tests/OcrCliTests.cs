using System.Diagnostics;
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
        Assert.Equal(2, await d.DispatchAsync(["ocr"]));
    }

    [Fact]
    public async Task Ocr_MissingFile_ReturnsExit1_BeforeAnySidecar()
    {
        var d = new CommandDispatcher();
        OcrCommands.Register(d);
        Assert.Equal(1, await d.DispatchAsync(["ocr", Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.png")]));
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

        string fixture = CreateTextFixturePng();
        try
        {
            if (!TryWarmUpModel(python, repoRoot, fixture)) return; // no paddle/model/network -> skip

            var d = new CommandDispatcher();
            OcrCommands.Register(d);
            var (stdout, stderr, exit) = await RunDispatcherAsync(d, ["ocr", fixture]);
            Assert.True(exit == 0, $"ocr CLI failed (exit {exit}): {stderr}");
            var json = JsonNode.Parse(stdout)!;
            Assert.NotEmpty(json["spots"]!.AsArray());
            Assert.Contains("HELLO", string.Join(" ", json["spots"]!.AsArray()
                .Select(s => s!["text"]!.GetValue<string>())).ToUpperInvariant());
            Assert.NotNull(json["new_texts"]);
        }
        finally { File.Delete(fixture); }
    }

    /// <summary>Runs one python snippet that loads PaddleOCR and OCRs the fixture (warms the model cache).</summary>
    private static bool TryWarmUpModel(string python, string repoRoot, string fixturePng)
    {
        string script = Path.Combine(Path.GetTempPath(), $"retran-warmup-{Guid.NewGuid():N}.py");
        File.WriteAllText(script, """
            import sys
            import numpy as np
            from PIL import Image
            from paddleocr import PaddleOCR
            img = np.array(Image.open(sys.argv[1]).convert("RGB"))[:, :, ::-1]
            PaddleOCR(lang="en").predict(img)
            print("WARM_OK")
            """);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add(fixturePng);
            using var p = Process.Start(psi)!;
            // generous: first run downloads the model (~15-20MB)
            if (!p.WaitForExit(300_000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            return p.ExitCode == 0 && p.StandardOutput.ReadToEnd().Contains("WARM_OK");
        }
        catch { return false; }
        finally { if (File.Exists(script)) File.Delete(script); }
    }

    /// <summary>Draws HELLO RETRAN on white; returns the temp PNG path (caller deletes).</summary>
    private static string CreateTextFixturePng()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retran-ocr-fixture-{Guid.NewGuid():N}.png");
        using var bmp = new System.Drawing.Bitmap(640, 96);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.White);
        using var font = new System.Drawing.Font("Arial", 48f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
        g.DrawString("HELLO RETRAN", font, System.Drawing.Brushes.Black, 10f, 20f);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    private static async Task<(string Stdout, string Stderr, int Exit)> RunDispatcherAsync(
        CommandDispatcher d, string[] args)
    {
        var origOut = Console.Out;
        var origErr = Console.Error;
        using var so = new StringWriter();
        using var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se); // brief said SetErr — .NET API is SetError (no SetErr exists)
        try
        {
            // Brief returned (so, se, await dispatch) — tuple elements eval L→R, so strings
            // would be captured BEFORE dispatch writes. Await first, then snapshot.
            int exit = await d.DispatchAsync(args);
            return (so.ToString(), se.ToString(), exit);
        }
        finally { Console.SetOut(origOut); Console.SetError(origErr); }
    }
}
