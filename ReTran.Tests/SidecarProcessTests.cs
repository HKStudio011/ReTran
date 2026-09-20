using System.Text.Json.Nodes;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class SidecarProcessTests
{
    [Fact]
    public async Task Ping_RoundTrips_ThroughRealPythonProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // sidecar spawn is Windows-focused for M0
        if (FindRepoRoot() is not string repoRoot) return; // no repo layout -> skip
        string pythonExe = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return; // venv not created yet (Task 4: uv sync) -> skip
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        JsonNode? pong = await sp.CallAsync("sidecar.ping", null, CancellationToken.None);
        Assert.Equal("pong", pong!.GetValue<string>());
    }

    /// <summary>Walks up from the test bin dir to the repo root (dir containing "ReTran.OCR/pyproject.toml").</summary>
    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ReTran.OCR", "pyproject.toml")))
            d = d.Parent;
        return d?.FullName;
    }
}
