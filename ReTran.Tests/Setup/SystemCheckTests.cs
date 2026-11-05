using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="SystemCheck"/>: the GPU/CPU flavor decision with the CUDA wheel ladder
/// (spec §3.5, §5.7; driver floors 580.00/576.02/560.76 from CUDA Toolkit Release Notes Table 3 /
/// Table 2), the hard-fail gates (arch, uv.exe, disk per flavor, network probes) and the
/// WMI→product driver-version mapping feeding the ladder. All side effects (adapters, disk, network,
/// file existence) are faked — <c>Run</c> itself is pure.
/// Test cho <see cref="SystemCheck"/>: quyết định flavor GPU/CPU kèm bậc thang wheel CUDA (spec §3.5,
/// §5.7; ngưỡng driver 580.00/576.02/560.76 theo CUDA Toolkit Release Notes Table 3 / Table 2), các
/// cổng fail cứng (arch, uv.exe, đĩa theo flavor, probe mạng) và phép ánh xạ WMI→product version nuôi
/// bậc thang đó. Mọi side effect (adapter, đĩa, mạng, tồn tại file) đều là fake — bản thân <c>Run</c>
/// thuần khiết.
/// </summary>
public class SystemCheckTests
{
    private const long GB = 1024L * 1024 * 1024;

    // Ground truth đo trên máy dev 2026-11-05: WMI DriverVersion "32.0.16.1742" == nvidia-smi 617.42.
    private static readonly VideoAdapterInfo NvidiaCurrent =
        new("NVIDIA GeForce RTX 4070", true, "32.0.16.1742");

    private static IVideoAdapterSource Adapters(params VideoAdapterInfo[] items) => new FakeAdapters(items);

    private sealed class FakeAdapters(VideoAdapterInfo[] items) : IVideoAdapterSource
    {
        public IReadOnlyList<VideoAdapterInfo> GetAdapters() => items;
    }

    /// <summary>
    /// NVIDIA adapter with the given product driver version encoded into the WMI/D3D form the source
    /// supplies ("NN.0.B.TTTT", TTTT = major%100 * 100 + minor), e.g. (576, 2) → "31.0.15.7602".
    /// Adapter NVIDIA với product driver version cho trước, mã hóa về dạng WMI/D3D mà nguồn cung cấp
    /// ("NN.0.B.TTTT", TTTT = major%100 * 100 + minor), vd (576, 2) → "31.0.15.7602".
    /// </summary>
    private static VideoAdapterInfo NvidiaWithDriver(int productMajor, int productMinor) =>
        new("NVIDIA GeForce RTX 4060", true, $"31.0.15.{productMajor % 100:00}{productMinor:00}");

    private static readonly VideoAdapterInfo AmdDiscrete =
        new("AMD Radeon RX 7800 XT", true, "31.0.25002.1001");

    private static readonly VideoAdapterInfo AmdIgpu =
        new("AMD Radeon Graphics", false, "31.0.25002.1001");

    private static readonly VideoAdapterInfo IntelIgpu =
        new("Intel(R) UHD Graphics 770", false, "31.0.101.2115");

    /// <summary>
    /// File-existence fake keyed by file name: only uv.exe and python.exe paths get a verdict,
    /// everything else is reported absent.
    /// Fake tồn tại file tra theo tên file: chỉ đường dẫn uv.exe và python.exe có kết luận, mọi path khác
    /// báo là không tồn tại.
    /// </summary>
    private static Func<string, bool> FileSystem(bool uvPresent = true, bool venvPresent = false) => path =>
        path.EndsWith("uv.exe", StringComparison.OrdinalIgnoreCase) ? uvPresent
        : path.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase) ? venvPresent
        : false;

    private static SystemCheckResult Check(
        IVideoAdapterSource adapters,
        long freeBytes = 500 * GB,
        bool allHostsReachable = true,
        bool uvPresent = true,
        bool venvPresent = false)
        => SystemCheck.Run(adapters, _ => freeBytes, _ => allHostsReachable, FileSystem(uvPresent, venvPresent));

    [Fact]
    public void Gpu_WhenNvidiaWithNewDriver()
    {
        var result = Check(Adapters(NvidiaCurrent));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu130", result.CudaWheel); // 617.42 ≥ 580.00
        Assert.Null(result.Notice);
        Assert.True(result.UvExePresent);
    }

    // ----- bậc thang CUDA wheel: boundary theo từng hàng của bảng driver → wheel -----

    [Fact]
    public void Gpu_Cu130_WhenDriverAtLeast580()
    {
        var result = Check(Adapters(NvidiaWithDriver(580, 12))); // driver 580.12

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu130", result.CudaWheel);
    }

    [Fact]
    public void Gpu_Cu129_WhenDriverExactly576_02()
    {
        var result = Check(Adapters(NvidiaWithDriver(576, 2))); // 576.02 = CUDA 12.9 GA floor

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu129", result.CudaWheel);
    }

    [Fact]
    public void Gpu_Cu126_WhenDriverJustBelow576_02()
    {
        var result = Check(Adapters(NvidiaWithDriver(575, 99)));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu126", result.CudaWheel);
    }

    [Fact]
    public void Gpu_Cu126_WhenDriverExactly560_76()
    {
        var result = Check(Adapters(NvidiaWithDriver(560, 76))); // 560.76 = CUDA 12.6 GA floor

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu126", result.CudaWheel);
    }

    [Fact]
    public void Cpu_WithNotice_WhenDriverJustBelow560_76()
    {
        var result = Check(Adapters(NvidiaWithDriver(560, 75)));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("560.76", result.Notice);
    }

    [Fact]
    public void Cpu_WithNotice_WhenNvidiaDriverUnreadable()
    {
        var unknownDriver = new VideoAdapterInfo("NVIDIA GeForce GT 610", true, null);

        var result = Check(Adapters(unknownDriver));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public void Gpu_WhenNvidiaAlongsideIgpu()
    {
        // Optimus: iGPU Intel + NVIDIA rời → vẫn phải là GPU flavor.
        var result = Check(Adapters(IntelIgpu, NvidiaCurrent));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu130", result.CudaWheel);
    }

    // ----- AMD / iGPU notices (spec §5.7) -----

    [Fact]
    public void Cpu_WithAmdNotice_WhenDiscreteAmd()
    {
        var result = Check(Adapters(AmdDiscrete));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("AMD", result.Notice);
        Assert.Contains("CPU", result.Notice);
    }

    [Fact]
    public void Cpu_LightNotice_WhenOnlyIgpu()
    {
        var result = Check(Adapters(IntelIgpu));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.NotNull(result.Notice);
        // spec §5.7: chỉ adapter RỜI mới được nhắc "card AMD"; iGPU dùng thông báo chung.
        Assert.DoesNotContain("AMD", result.Notice);
    }

    [Fact]
    public void Cpu_LightNotice_WhenAmdIgpuOnly()
    {
        var result = Check(Adapters(AmdIgpu));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.NotNull(result.Notice);
        Assert.DoesNotContain("AMD", result.Notice);
    }

    // ----- cổng fail cứng -----

    [Fact]
    public void Fail_WhenUvExeMissing()
    {
        var result = Check(Adapters(NvidiaCurrent), uvPresent: false);

        Assert.False(result.Ok);
        Assert.False(result.UvExePresent);
        Assert.Null(result.CudaWheel);
        Assert.Contains("uv.exe", result.FailureReason);
    }

    [Fact]
    public void Fail_WhenDiskBelowCpuThreshold()
    {
        var result = Check(Adapters(IntelIgpu), freeBytes: 4 * GB);

        Assert.False(result.Ok);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("5 GB", result.FailureReason);
    }

    [Fact]
    public void Fail_WhenDiskBelowGpuThreshold_ButCpuWouldPass()
    {
        var gpuTooLittle = Check(Adapters(NvidiaCurrent), freeBytes: 7 * GB);

        Assert.False(gpuTooLittle.Ok);
        Assert.Contains("8 GB", gpuTooLittle.FailureReason);

        // Cùng 7 GB đó nhưng chỉ có iGPU → flavor CPU với ngưỡng 5 GB → pass.
        Assert.True(Check(Adapters(IntelIgpu), freeBytes: 7 * GB).Ok);
    }

    [Fact]
    public void Fail_NamesUnreachableHost()
    {
        var probed = new List<string>();

        var result = SystemCheck.Run(
            Adapters(NvidiaCurrent),
            _ => 500 * GB,
            host =>
            {
                probed.Add(host);
                return host != "github.com";
            },
            FileSystem());

        Assert.False(result.Ok);
        Assert.Contains("github.com", result.FailureReason);
        Assert.Contains("github.com", probed);
    }

    [Fact]
    public void Result_FlagsExistingVenv()
    {
        var result = Check(Adapters(NvidiaCurrent), venvPresent: true);

        Assert.True(result.UvExePresent);
        Assert.True(result.VenvPresent);
    }

    // ----- mapping + ladder helpers -----

    [Fact]
    public void MapWmiDriverVersion_MatchesDevMachineGroundTruth()
    {
        // Máy dev đo ngày 2026-11-05: WMI 32.0.16.1742 ↔ nvidia-smi driver_version 617.42.
        Assert.True(SystemCheck.TryGetNvidiaProductVersion("32.0.16.1742", out var measured));
        Assert.Equal((617, 42), measured);

        Assert.True(SystemCheck.TryGetNvidiaProductVersion("31.0.15.6076", out var floor));
        Assert.Equal((560, 76), floor);

        Assert.False(SystemCheck.TryGetNvidiaProductVersion(null, out _));
        Assert.False(SystemCheck.TryGetNvidiaProductVersion("n/a", out _));
        // D3D major 27 (họ driver 4xx tiền CUDA 12): ngoài khoảng ánh xạ → không hỗ trợ CUDA 12.x.
        Assert.False(SystemCheck.TryGetNvidiaProductVersion("27.21.14.5609", out _));
    }

    [Fact]
    public void CudaWheelForDriverVersion_FollowsLadderBoundaries()
    {
        Assert.Equal("cu130", SystemCheck.CudaWheelForDriverVersion(580, 0));
        Assert.Equal("cu130", SystemCheck.CudaWheelForDriverVersion(617, 42));
        Assert.Equal("cu129", SystemCheck.CudaWheelForDriverVersion(579, 99));
        Assert.Equal("cu129", SystemCheck.CudaWheelForDriverVersion(576, 2));
        Assert.Equal("cu126", SystemCheck.CudaWheelForDriverVersion(576, 1));
        Assert.Equal("cu126", SystemCheck.CudaWheelForDriverVersion(560, 76));
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(560, 75));
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(457, 1));
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(560, 150)); // ngoài format minor 2 chữ số → không tin
    }

    [Fact]
    public void IsDiscreteName_AmdRequiresRxPattern()
    {
        Assert.True(VideoAdapterSource.IsDiscreteName("AMD Radeon RX 7800 XT"));
        Assert.True(VideoAdapterSource.IsDiscreteName("NVIDIA GeForce RTX 5060 Ti"));
        Assert.False(VideoAdapterSource.IsDiscreteName("AMD Radeon Graphics"));
        Assert.False(VideoAdapterSource.IsDiscreteName("Intel(R) UHD Graphics 770"));
        Assert.False(VideoAdapterSource.IsDiscreteName("Microsoft Basic Display Adapter"));
    }
}
