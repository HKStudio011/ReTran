using System.Runtime.InteropServices;
using ReTran_App.Services;
using ReTran_App.Services.Setup;

/// <summary>
/// Tests for <see cref="SystemCheck"/>: the GPU/CPU flavor decision with the single cu129 driver floor
/// (spec §3.5, §5.7; 576.02 = CUDA 12.9 GA from CUDA Toolkit Release Notes Table 3 — the cu130/cu126
/// rungs were removed in Task 11c), the hard-fail gates (arch, uv.exe, disk per flavor, network probes)
/// and the WMI→product driver-version mapping feeding the floor. The mapping fixtures are real NVIDIA
/// INF/WMI strings (the product family lives in the THIRD segment — the driver branch — not in the DDI
/// major), so a fixture can never hide a mapping mistake again. All side effects (adapters, disk,
/// network, file existence) are faked — <c>Run</c> itself is pure.
/// Test cho <see cref="SystemCheck"/>: quyết định flavor GPU/CPU kèm sàn driver cu129 duy nhất (spec
/// §3.5, §5.7; 576.02 = CUDA 12.9 GA theo CUDA Toolkit Release Notes Table 3 — nấc cu130/cu126 đã bỏ ở
/// Task 11c), các cổng fail cứng (arch, uv.exe, đĩa theo flavor, probe mạng) và phép ánh xạ WMI→product
/// version nuôi sàn đó. Fixture ánh xạ là chuỗi INF/WMI NVIDIA thật (họ sản phẩm nằm ở segment THỨ BA —
/// branch driver — không phải DDI major), nên fixture không bao giờ che được lỗi ánh xạ nữa. Mọi side
/// effect (adapter, đĩa, mạng, tồn tại file) đều là fake — bản thân <c>Run</c> thuần khiết.
/// </summary>
public class SystemCheckTests
{
    private const long GB = 1024L * 1024 * 1024;

    // Message wizard giờ là key + args (SetupMessage) — helper resolve đúng như UI (I18nService, VI)
    // để các assert chuỗi hiện có vẫn kiểm tra nguyên văn thông điệp người dùng thấy.
    private static string Text(SetupMessage? message)
    {
        var i18n = new I18nService();
        i18n.Set("vi");
        return message is null ? string.Empty : i18n.Fmt(message.Key, message.Args.ToArray());
    }

    // Chuỗi WMI đo thật trên máy dev ngày 2026-11-05: DriverVersion "32.0.16.1742" == nvidia-smi
    // driver_version 617.42 (branch 16 ⇒ họ 6xx). Máy dev hiện mang RTX 5060 Ti.
    // Measured on the dev machine 2026-11-05: WMI DriverVersion "32.0.16.1742" == nvidia-smi 617.42
    // (branch 16 ⇒ 6xx family). The dev machine currently has an RTX 5060 Ti.
    private static readonly VideoAdapterInfo NvidiaCurrent =
        new("NVIDIA GeForce RTX 5060 Ti", true, "32.0.16.1742");

    private static IVideoAdapterSource Adapters(params VideoAdapterInfo[] items) => new FakeAdapters(items);

    private sealed class FakeAdapters(VideoAdapterInfo[] items) : IVideoAdapterSource
    {
        public IReadOnlyList<VideoAdapterInfo> GetAdapters() => items;
    }

    /// <summary>
    /// NVIDIA adapter carrying the given product driver version encoded into the real INF/WMI form
    /// "DDI.0.BRANCH.TTTT": the branch segment (14/15/16 ⇔ product 4xx/5xx/6xx) carries the product
    /// family, TTTT = (major % 100) * 100 + minor, and DDI major is pinned to 32 because it carries no
    /// product information at all (see <see cref="SystemCheck.TryGetNvidiaProductVersion")). Only for
    /// products 4xx–6xx; anything else is a fixture bug and throws.
    /// Adapter NVIDIA mang product driver version cho trước, mã hóa đúng dạng INF/WMI thật
    /// "DDI.0.BRANCH.TTTT": segment branch (14/15/16 ⇔ product 4xx/5xx/6xx) mang họ sản phẩm,
    /// TTTT = (major % 100) * 100 + minor, còn DDI major ghim cứng 32 vì nó không mang thông tin sản
    /// phẩm nào (xem <see cref="SystemCheck.TryGetNvidiaProductVersion")). Chỉ dùng cho product
    /// 4xx–6xx; ngoài khoảng là fixture bug và ném exception.
    /// </summary>
    private static VideoAdapterInfo NvidiaWithDriver(int productMajor, int productMinor)
    {
        int branch = productMajor switch
        {
            >= 600 => 16,
            >= 500 => 15,
            >= 400 => 14,
            _ => throw new ArgumentOutOfRangeException(
                nameof(productMajor), productMajor, "fixture chỉ dựng được driver NVIDIA họ 4xx–6xx"),
        };
        return new VideoAdapterInfo(
            "NVIDIA GeForce RTX 4060", true, $"32.0.{branch}.{productMajor % 100:00}{productMinor:00}");
    }

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
        Assert.Equal("cu129", result.CudaWheel); // 617.42 ≥ 576.02
        Assert.Null(result.Notice);
        Assert.True(result.UvExePresent);
    }

    // ----- sàn driver cu129 duy nhất: boundary 576.02 (nấc cu130/cu126 đã bỏ, Task 11c) -----

    [Fact]
    public void Gpu_Cu129_WhenDriverAbove580()
    {
        // Nấc cu130 đã bỏ (đo thật 2026-11-07): cần họ gói nvidia-*-cu13 khác + driver ≥ 580 — driver
        // 580.12 giờ vẫn chỉ chọn "cu129". The cu130 rung was removed: it needs the different
        // nvidia-*-cu13 package family plus a driver >= 580 — a 580.12 driver now still selects only
        // "cu129".
        var result = Check(Adapters(NvidiaWithDriver(580, 12))); // driver 580.12

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal("cu129", result.CudaWheel);
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
    public void Cpu_WithNotice_WhenDriverJustBelow576_02()
    {
        var result = Check(Adapters(NvidiaWithDriver(575, 99))); // 575.99 — dưới sàn mới 576.02

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("576.02", Text(result.Notice));
    }

    [Fact]
    public void Cpu_WithNotice_WhenDriverAtOldCu126Floor()
    {
        // 560.76 từng là sàn cu126 (wheel đã bỏ) — driver này giờ chỉ còn ra CPU + notice nêu sàn mới.
        // 560.76 used to be the cu126 floor (rung removed) — this driver now only lands on CPU plus the
        // notice quoting the new floor.
        var result = Check(Adapters(NvidiaWithDriver(560, 76)));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("576.02", Text(result.Notice));
    }

    [Fact]
    public void Cpu_WithNotice_RerunSetupHint_WhenDriverAt576_01()
    {
        // Biên sát sàn 576.02: notice phải nêu mốc 576.02 và gợi ý cập nhật driver + chạy lại Setup —
        // đúng yêu cầu user (cảnh báo bản CPU, có thể đổi sang bản GPU sau khi nâng driver).
        // Sharp boundary at the 576.02 floor: the notice must quote 576.02 and offer updating the driver
        // plus re-running Setup — the user requirement (warn about the CPU flavor, switchable to GPU
        // after a driver update).
        var result = Check(Adapters(NvidiaWithDriver(576, 1)));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("576.02", Text(result.Notice));
        Assert.Contains("chạy lại Setup", Text(result.Notice));
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
        Assert.Equal("cu129", result.CudaWheel);
    }

    // ----- NVIDIA RỜI mới được xét flavor GPU (đối xứng với nhánh AMD, spec §3.5) -----

    [Fact]
    public void Cpu_GeneralNotice_WhenOnlyNonDiscreteNvidiaAdapter()
    {
        // Adapter NVIDIA không rời (iGPU đời cũ / adapter ảo đặt tên "NVIDIA …") với driver mới nhất
        // vẫn không được chọn flavor GPU; notice phải là notice chung, không khẳng định "driver chưa đủ mới".
        // A non-discrete NVIDIA adapter (old iGPU / virtual adapter named "NVIDIA …") with the newest
        // driver still must not pick the GPU flavor, and the notice must be the generic one — it must
        // not claim the driver is too old.
        var virtualNvidia = new VideoAdapterInfo("NVIDIA Virtual Display Adapter", false, "32.0.16.2114");

        var result = Check(Adapters(virtualNvidia));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.DoesNotContain("576.02", Text(result.Notice));
        Assert.Contains("CPU", Text(result.Notice));
    }

    [Fact]
    public void Cpu_WhenNonDiscreteNvidiaHasNewDriver_ButDiscreteNvidiaDriverIsOld()
    {
        // Adapter ảo tên "NVIDIA …" driver 621.14 + card NVIDIA rời driver 536.99 → không được mượn wheel
        // của adapter ảo: CPU + notice driver.
        // Virtual "NVIDIA …" adapter on 621.14 + a discrete NVIDIA card on 536.99: the virtual adapter's
        // wheel must not leak in → CPU + the driver notice.
        var virtualNvidia = new VideoAdapterInfo("NVIDIA Virtual Display Adapter", false, "32.0.16.2114");
        var discreteOld = new VideoAdapterInfo("NVIDIA GeForce RTX 2060", true, "31.0.15.3699");

        var result = Check(Adapters(virtualNvidia, discreteOld));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.Contains("576.02", Text(result.Notice));
    }

    // ----- chuỗi INF/WMI NVIDIA thật: driver → wheel (regression 560.94 từng ra cu130; nay chỉ còn cu129) -----

    [Theory]
    [InlineData("32.0.15.7666", "cu129")] // 576.66 — vừa trên sàn 576.02
    [InlineData("32.0.15.8163", "cu129")] // 581.63 — bug cũ suy thành 681.63 (không tồn tại); nấc cu130 đã bỏ
    [InlineData("32.0.16.2114", "cu129")] // 621.14 — máy dev đo 2026-11-06
    [InlineData("32.0.16.1742", "cu129")] // 617.42 — máy dev đo 2026-11-05
    public void Gpu_HighestWheelTheDriverSupports_RealWmiStrings(string wmiDriverVersion, string expectedWheel)
    {
        var adapter = new VideoAdapterInfo("NVIDIA GeForce RTX 4060", true, wmiDriverVersion);

        var result = Check(Adapters(adapter));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Gpu, result.Flavor);
        Assert.Equal(expectedWheel, result.CudaWheel);
        Assert.Null(result.Notice);
    }

    [Theory]
    [InlineData("32.0.15.6094")] // 560.94 — dưới sàn mới 576.02 (từng ra cu126 trước khi bỏ nấc)
    [InlineData("31.0.15.3699")] // 536.99 — dưới sàn 576.02
    [InlineData("27.21.14.5609")] // 456.09 — decode được nhưng dưới sàn
    [InlineData("32.0.17.1234")] // branch 17 chưa tồn tại ⇒ từ chối
    [InlineData("30.0.13.6521")] // branch 13 (họ 3xx tiền CUDA 12) ⇒ từ chối
    public void Cpu_WithDriverNotice_RealWmiStrings(string wmiDriverVersion)
    {
        var adapter = new VideoAdapterInfo("NVIDIA GeForce RTX 2060", true, wmiDriverVersion);

        var result = Check(Adapters(adapter));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.Null(result.CudaWheel);
        Assert.NotNull(result.Notice);
        Assert.Contains("576.02", Text(result.Notice));
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
        Assert.Contains("AMD", Text(result.Notice));
        Assert.Contains("CPU", Text(result.Notice));
    }

    [Fact]
    public void Cpu_LightNotice_WhenOnlyIgpu()
    {
        var result = Check(Adapters(IntelIgpu));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.NotNull(result.Notice);
        // spec §5.7: chỉ adapter RỜI mới được nhắc "card AMD"; iGPU dùng thông báo chung.
        Assert.DoesNotContain("AMD", Text(result.Notice));
    }

    [Fact]
    public void Cpu_LightNotice_WhenAmdIgpuOnly()
    {
        var result = Check(Adapters(AmdIgpu));

        Assert.True(result.Ok);
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
        Assert.NotNull(result.Notice);
        Assert.DoesNotContain("AMD", Text(result.Notice));
    }

    // ----- cổng fail cứng -----

    [Theory]
    // Cổng arch: chỉ X64 được đi tiếp. Máy ARM64 chạy Win32-on-ARM vẫn phải fail — uv.exe đi kèm và
    // wheel paddle chỉ có bản x64 — và thông báo không được khẳng định "Windows 32-bit".
    // Arch gate: only X64 proceeds. An ARM64 machine running Win32-on-ARM must still fail — the bundled
    // uv.exe and the paddle wheels are x64-only — and the message must not claim "Windows 32-bit".
    [InlineData(Architecture.X64, true)]
    [InlineData(Architecture.Arm64, false)]
    [InlineData(Architecture.X86, false)]
    public void ArchGate_RequiresX64(Architecture architecture, bool expectedOk)
    {
        var result = SystemCheck.Run(
            Adapters(NvidiaCurrent),
            _ => 500 * GB,
            _ => true,
            FileSystem(),
            osArchitecture: () => architecture);

        Assert.Equal(expectedOk, result.Ok);
        if (expectedOk)
        {
            return;
        }

        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain("32-bit", Text(result.FailureReason));
        Assert.Contains("x64", Text(result.FailureReason));
        Assert.Equal(OcrFlavor.Cpu, result.Flavor);
    }

    [Fact]
    public void ArchGate_DefaultSeamPassesOnThisMachine()
    {
        // Không seam ⇒ đọc RuntimeInformation.OSArchitecture thật; máy test là x64 nên phải qua cổng.
        // No seam ⇒ the real RuntimeInformation.OSArchitecture; the test machine is x64 so the gate passes.
        Assert.True(Check(Adapters(NvidiaCurrent)).Ok);
    }

    [Fact]
    public void Fail_WhenUvExeMissing()
    {
        var result = Check(Adapters(NvidiaCurrent), uvPresent: false);

        Assert.False(result.Ok);
        Assert.False(result.UvExePresent);
        Assert.Null(result.CudaWheel);
        Assert.Contains("uv.exe", Text(result.FailureReason));
    }

    [Fact]
    public void Fail_WhenDiskBelowCpuThreshold()
    {
        var result = Check(Adapters(IntelIgpu), freeBytes: 1 * GB);

        Assert.False(result.Ok);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("2 GB", Text(result.FailureReason));
    }

    [Fact]
    public void Fail_WhenDiskBelowGpuThreshold_ButCpuWouldPass()
    {
        var gpuTooLittle = Check(Adapters(NvidiaCurrent), freeBytes: 3 * GB);

        Assert.False(gpuTooLittle.Ok);
        Assert.Contains("8 GB", Text(gpuTooLittle.FailureReason));

        // Cùng 3 GB đó nhưng chỉ có iGPU → flavor CPU với ngưỡng 2 GB → pass.
        Assert.True(Check(Adapters(IntelIgpu), freeBytes: 3 * GB).Ok);
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
        Assert.Contains("github.com", Text(result.FailureReason));
        Assert.Contains("github.com", probed);
    }

    [Fact]
    public void Probe_VisitsEveryNetworkProbeHost()
    {
        var probed = new List<string>();

        var result = SystemCheck.Run(
            Adapters(NvidiaCurrent),
            _ => 500 * GB,
            host =>
            {
                probed.Add(host);
                return true;
            },
            FileSystem());

        Assert.True(result.Ok);
        Assert.Equal<string>(SystemCheck.NetworkProbeHosts, probed);
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

        // Đảo polarity của hàng này (Task 10): comment cũ nói DDI major 27 "ngoài khoảng ánh xạ" là SAI —
        // số trăm nằm ở branch segment (14 ⇒ họ 4xx), DDI major không mang thông tin đó. Chuỗi này decode
        // thành 456.09 (NVIDIA 456.71 ship INF 27.21.14.5671, cùng encoding). Ý định gốc của hàng test —
        // driver họ 4xx không bao giờ được chọn wheel CUDA — vẫn còn nguyên, và giờ được chốt ở tầng
        // flavor: 456.09 vẫn ra CPU + notice nêu sàn 576.02.
        // This row's polarity is inverted by Task 10: the old comment claimed DDI major 27 was "outside
        // the mapping range", which is WRONG — the product hundreds digit comes from the branch segment
        // (14 ⇒ 4xx family), DDI major carries no such information. The string decodes to 456.09 (NVIDIA
        // 456.71 ships INF 27.21.14.5671, same encoding). The row's original intent — a 4xx driver must
        // never pick a CUDA wheel — is preserved and now pinned at the flavor level: 456.09 still lands on
        // CPU plus the notice quoting the 576.02 floor.
        Assert.True(SystemCheck.TryGetNvidiaProductVersion("27.21.14.5609", out var legacy));
        Assert.Equal((456, 9), legacy);

        var legacyResult = Check(Adapters(new VideoAdapterInfo("NVIDIA GeForce RTX 2060", true, "27.21.14.5609")));
        Assert.Equal(OcrFlavor.Cpu, legacyResult.Flavor);
        Assert.Null(legacyResult.CudaWheel);
        Assert.Contains("576.02", Text(legacyResult.Notice));
    }

    [Theory]
    [InlineData("32.0.16.2114", 621, 14)] // đo trên máy dev 2026-11-06: nvidia-smi 621.14
    [InlineData("32.0.16.1742", 617, 42)] // đo trên máy dev 2026-11-05: nvidia-smi 617.42
    [InlineData("32.0.15.6094", 560, 94)] // Microsoft Update Catalog "NVIDIA - Display - 32.0.15.6094" = 560.94
    [InlineData("32.0.15.7666", 576, 66)] // branch 15 ⇒ 5xx, DDI major 32
    [InlineData("32.0.15.8163", 581, 63)] // Microsoft Update Catalog 10/2025; code cũ suy ra 681.63 (không tồn tại)
    [InlineData("31.0.15.3699", 536, 99)] // Microsoft Update Catalog 8/2023
    [InlineData("31.0.15.6076", 560, 76)] // same product as above row, different DDI major ⇒ same result
    [InlineData("27.21.14.5609", 456, 9)] // NVIDIA 456.71 ships INF 27.21.14.5671 ⇒ branch 14 ⇒ 4xx
    public void TryGetNvidiaProductVersion_DecodesRealWmiStrings(string wmiDriverVersion, int major, int minor)
    {
        Assert.True(SystemCheck.TryGetNvidiaProductVersion(wmiDriverVersion, out var actual));
        Assert.Equal((major, minor), actual);
    }

    [Theory]
    [InlineData("30.0.15.6094", 560, 94)] // DDI major 30 (synthetic) + branch 15 ⇒ vẫn 560.94: major không gate
    [InlineData("32.0.15.609", 506, 9)] // tail 3 chữ số ⇒ guard không chặn, math cho 506.09 ⇒ dưới mọi sàn
    public void TryGetNvidiaProductVersion_KeepsExistingGuardsAndMath(string wmiDriverVersion, int major, int minor)
    {
        Assert.True(SystemCheck.TryGetNvidiaProductVersion(wmiDriverVersion, out var actual));
        Assert.Equal((major, minor), actual);
    }

    [Theory]
    [InlineData("32.0.17.1234")] // branch 17: chưa có họ driver nào ⇒ từ chối, fail-safe CPU
    [InlineData("30.0.13.6521")] // branch 13 (họ 3xx tiền CUDA 12) ⇒ từ chối
    [InlineData("32.0.16.12345")] // tail 5 chữ số (> 9999) ⇒ từ chối
    [InlineData("32.0.16.100000")] // tail quá dài ⇒ từ chối
    [InlineData("32.0.15")] // thiếu segment ⇒ từ chối
    [InlineData("n/a")] // không phải số ⇒ từ chối
    public void TryGetNvidiaProductVersion_RejectsUnknownBranchAndMalformed(string? wmiDriverVersion)
    {
        Assert.False(SystemCheck.TryGetNvidiaProductVersion(wmiDriverVersion, out _));
    }

    [Fact]
    public void CudaWheelForDriverVersion_OnlyCu129AtOrAboveFloor()
    {
        Assert.Equal("cu129", SystemCheck.CudaWheelForDriverVersion(576, 2)); // = sàn CUDA 12.9 GA
        Assert.Equal("cu129", SystemCheck.CudaWheelForDriverVersion(580, 0)); // nấc cu130 cũ — giờ vẫn chỉ có cu129
        Assert.Equal("cu129", SystemCheck.CudaWheelForDriverVersion(617, 42));
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(576, 1)); // vừa dưới sàn → CPU + notice
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(560, 76)); // sàn cu126 cũ — giờ là CPU
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(457, 1));
        Assert.Null(SystemCheck.CudaWheelForDriverVersion(560, 150)); // ngoài format minor 2 chữ số → không tin
    }

    [Fact]
    public void Gpu_HighestWheelWins_RegardlessOfAdapterOrder()
    {
        // Nhiều card NVIDIA: card đạt sàn 576.02 phải thắng ở cả hai thứ tự dù card kia dưới sàn (wheel
        // null) — selection theo rank số nguyên của wheel, không theo thứ tự adapter.
        // Multiple NVIDIA cards: the qualifying card must win in both orders even though the other's
        // driver is below the floor (null wheel) — selection is by the wheel's integer rank, not by
        // adapter order.
        VideoAdapterInfo oldDriverCard = new("NVIDIA GeForce RTX 4060", true, "32.0.15.6094"); // 560.94 < 576.02 → null
        VideoAdapterInfo newDriverCard = new("NVIDIA GeForce RTX 5060 Ti", true, "32.0.16.2114"); // 621.14 → cu129

        Assert.Equal("cu129", Check(Adapters(oldDriverCard, newDriverCard)).CudaWheel);
        Assert.Equal("cu129", Check(Adapters(newDriverCard, oldDriverCard)).CudaWheel);
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

    [Theory]
    // Những tên NVIDIA mà gate R40 thật sự đọc từ WMI. Hàng "T4"/"RTX 6000 Ada" ghi lại GIỚI HẠN đã biết
    // của heuristic đặt tên: adapter data-center/pro không còn mang token GeForce/Quadro/Tesla/NVS bị coi
    // là không rời ⇒ flavor CPU (fail-safe, không bao giờ cài wheel driver không chạy nổi).
    // NVIDIA names the R40 gate actually reads from WMI. The "T4" / "RTX 6000 Ada" rows record the known
    // limitation of the name heuristic: data-center/pro adapters no longer carrying a
    // GeForce/Quadro/Tesla/NVS token count as non-discrete ⇒ CPU flavor (fail-safe: it never installs a
    // wheel the driver cannot run).
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce GTX 1650", true)]
    [InlineData("NVIDIA Quadro P400", true)]
    [InlineData("NVIDIA Tesla V100", true)]
    [InlineData("NVIDIA NVS 510", true)]
    [InlineData("NVIDIA T4", false)]
    [InlineData("NVIDIA RTX 6000 Ada Generation", false)]
    [InlineData("NVIDIA Virtual Display Adapter", false)]
    public void IsDiscreteName_NvidiaNamingGateCases(string name, bool expected)
    {
        Assert.Equal(expected, VideoAdapterSource.IsDiscreteName(name));
    }
}
