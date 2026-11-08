using System.Text.Json.Nodes;
using ReTran.Core.OcrClient;

namespace RetranCore.Tests;

/// <summary>
/// Real-sidecar test for the new <c>sidecar.device</c> verb (addendum §3): starts the actual sidecar
/// from the repo venv via the existing helpers and asserts it reports the machine's paddle device plus
/// compute capability. Measured on the dev machine 2026-11-08: GPU flavor, RTX 5060 Ti, CC 12.0 —
/// <c>paddle.device.cuda.get_device_capability()</c> returned (12, 0). Skips politely outside the repo
/// layout or on a broken venv (never a silent pass).
/// Test sidecar thật cho verb mới <c>sidecar.device</c> (addendum §3): khởi động sidecar thật từ venv
/// repo bằng helper có sẵn và assert nó báo đúng device paddle của máy kèm compute capability. Đo trên
/// máy dev 2026-11-08: flavor GPU, RTX 5060 Ti, CC 12.0 — <c>paddle.device.cuda.get_device_capability()</c>
/// trả về (12, 0). Bỏ qua lịch sự khi ngoài layout repo hoặc venv hỏng (không bao giờ pass im lặng).
/// </summary>
public class OcrDeviceVerbTests
{
    [Fact]
    public async Task Sidecar_Device_ReturnsGpuDeviceAndCapability()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("real-sidecar E2E is Windows-only (venv layout + sidecar spawn)");
        if (!OcrTestSupport.TryResolveRepoWithVenv(out string repoRoot, out string python))
            Assert.Skip("no ancestor of AppContext.BaseDirectory has both ReTran.OCR/pyproject.toml and ReTran.OCR/.venv/Scripts/python.exe (repo layout or uv venv missing)");

        // The first call pays the lazy paddle import + CUDA init, so give it a generous window.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var sp = await SidecarProcess.StartAsync(python, repoRoot, cts.Token);
        JsonNode? result = await sp.CallAsync("sidecar.device", null, cts.Token);

        Assert.NotNull(result);
        string device = result!["device"]!.GetValue<string>();
        Assert.True(device.StartsWith("gpu:", StringComparison.Ordinal),
            $"expected a GPU device on the dev machine, got '{device}'");
        JsonArray capability = result["capability"]!.AsArray();
        Assert.Equal(2, capability.Count);
        // Measured on this machine: CC 12.0 — and comfortably above the wizard's 7.5 floor.
        Assert.Equal([12, 0], capability.Select(static n => n!.GetValue<int>()).ToArray());
    }
}
