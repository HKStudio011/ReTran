using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using ReTran.Core.Ocr;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class SidecarProcessTests
{
    [Fact]
    public void ResponseLine_DeliversDetachedClone_SerializableResponse()
    {
        JsonNode? delivered = null;
        string? deliveredId = null;
        bool handled = SidecarProcess.TryRouteResponseLine(
            "{\"jsonrpc\":\"2.0\",\"id\":\"7\",\"result\":{\"params\":[\"a\"]}}",
            (id, result) => { deliveredId = id; delivered = result; },
            (_, ex) => throw ex);

        Assert.True(handled);
        Assert.Equal("7", deliveredId);
        // Trước fix: node còn parent (con của envelope) → dòng dưới ném
        // InvalidOperationException "The node already has a parent."
        string line = JsonRpc.SerializeResponse(new JsonRpcResponse(deliveredId!, delivered, null));
        Assert.Contains("\"params\":[\"a\"]", line);
    }

    [Fact]
    public async Task Ping_RoundTrips_ThroughRealPythonProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // sidecar spawn is Windows-focused for M0
        if (OcrBootstrap.FindRepoRoot() is not string repoRoot) return; // no repo layout -> skip
        string pythonExe = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return; // venv not created yet (Task 4: uv sync) -> skip
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        JsonNode? pong = await sp.CallAsync("sidecar.ping", null, CancellationToken.None);
        Assert.Equal("pong", pong!.GetValue<string>());
    }

    [Fact]
    public async Task Spot_InvalidImage_SurfacesSidecarErrorAsException()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (OcrBootstrap.FindRepoRoot() is not string repoRoot) return;
        string pythonExe = Path.Combine(repoRoot, "ReTran.OCR", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return;
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        var p = new JsonObject { ["image"] = "@@@" };
        var ex = await Assert.ThrowsAsync<SidecarRpcException>(() =>
            sp.CallAsync("ocr.spot", p, CancellationToken.None));
        Assert.Equal(-32602, ex.Code);
        Assert.Contains("invalid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
