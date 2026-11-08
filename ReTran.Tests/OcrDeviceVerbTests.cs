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
        // A CPU-flavour venv (or a machine without a visible GPU) is a valid environment — skip,
        // never fail: this test only verifies the gate's input on a GPU setup.
        if (!device.StartsWith("gpu:", StringComparison.Ordinal))
            Assert.Skip($"sidecar reports a non-GPU device ('{device}') — no capability to verify here");

        JsonArray capability = result["capability"]!.AsArray();
        Assert.Equal(2, capability.Count);
        int major = capability[0]!.GetValue<int>();
        int minor = capability[1]!.GetValue<int>();
        // Assert the gate's OWN floor (major*10+minor >= 75 — cu129 fatbins sm_75/80/86/89/120), not a
        // machine-specific tuple: on this dev machine the measured value is CC 12.0 (RTX 5060 Ti).
        Assert.True(major * 10 + minor >= 75,
            $"GPU device '{device}' reports capability {major}.{minor}, below the wizard's 7.5 floor");
    }
}
