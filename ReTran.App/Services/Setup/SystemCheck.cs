namespace ReTran_App.Services.Setup;

/// <summary>
/// OCR wheel flavor the setup wizard should install for this machine.
/// Flavor wheel OCR mà wizard cài trên máy này.
/// </summary>
public enum OcrFlavor
{
    /// <summary>
    /// paddlepaddle-gpu wheel — the CUDA variant is in <see cref="SystemCheckResult.CudaWheel"/>.
    /// Wheel paddlepaddle-gpu — biến thể CUDA nằm ở <see cref="SystemCheckResult.CudaWheel"/>.
    /// </summary>
    Gpu,

    /// <summary>
    /// paddlepaddle CPU wheel from PyPI — works on any machine.
    /// Wheel paddlepaddle CPU từ PyPI — chạy được trên mọi máy.
    /// </summary>
    Cpu,
}

/// <summary>
/// One display adapter as seen by the system: display name, discrete flag, and driver version in the
/// raw form the source supplies it (WMI gives NVIDIA's D3D/INF form "NN.0.B.TTTT" — see
/// <see cref="SystemCheck.TryGetNvidiaProductVersion"/> for the mapping to NVIDIA's product-version form).
/// Một display adapter hệ thống nhìn thấy: tên hiển thị, cờ card rời, phiên bản driver ở dạng thô mà
/// nguồn cung cấp (WMI trả dạng D3D/INF "NN.0.B.TTTT" của NVIDIA — xem
/// <see cref="SystemCheck.TryGetNvidiaProductVersion"/> để biết cách ánh xạ về dạng product version).
/// </summary>
/// <param name="Name">
/// Adapter display name, e.g. "NVIDIA GeForce RTX 5060 Ti".
/// Tên hiển thị của adapter, vd "NVIDIA GeForce RTX 5060 Ti".
/// </param>
/// <param name="IsDiscrete">
/// True for a discrete/dedicated adapter; false for an iGPU or when unknown.
/// True là card rời; false là iGPU hoặc không xác định được.
/// </param>
/// <param name="DriverVersion">
/// Raw driver version string, or null when the source could not read one.
/// Chuỗi phiên bản driver dạng thô, hoặc null khi nguồn không đọc được.
/// </param>
public sealed record VideoAdapterInfo(string Name, bool IsDiscrete, string? DriverVersion);

/// <summary>
/// Source of the display-adapter inventory consumed by <see cref="SystemCheck"/>. Implementations must
/// be defensive: an unreadable source returns an empty list, never throws.
/// Nguồn cung cấp danh sách display adapter cho <see cref="SystemCheck"/>. Implementation phải phòng
/// thủ: nguồn không đọc được thì trả danh sách rỗng, không được ném exception.
/// </summary>
public interface IVideoAdapterSource
{
    /// <summary>
    /// All display adapters present, in any order; empty when none can be read.
    /// Toàn bộ display adapter hiện có, thứ tự bất kỳ; rỗng khi không đọc được nguồn nào.
    /// </summary>
    IReadOnlyList<VideoAdapterInfo> GetAdapters();
}

/// <summary>
/// Outcome of the first-run system check: the wheel flavor (plus CUDA variant) to install, an optional
/// user-facing notice, and the hard-gate failure reason when the wizard must not start.
/// Kết quả check system lần đầu chạy: flavor wheel (kèm biến thể CUDA) sẽ cài, notice tùy chọn cho
/// người dùng, và lý do fail cổng cứng khi wizard không được phép bắt đầu.
/// </summary>
/// <param name="Ok">
/// True when every hard gate passed and the wizard may continue.
/// True khi mọi cổng cứng đạt và wizard được tiếp tục.
/// </param>
/// <param name="Flavor">
/// OCR wheel flavor selected for this machine.
/// Flavor wheel OCR được chọn cho máy này.
/// </param>
/// <param name="Notice">
/// Advisory text to show the user when the decision downgraded to CPU (driver/AMD/no-NVIDIA); null on GPU.
/// Thông báo advisory cho người dùng khi quyết định hạ xuống CPU (driver/AMD/không NVIDIA); null khi GPU.
/// </param>
/// <param name="FailureReason">
/// Non-null only when <paramref name="Ok"/> is false; names the failed gate (host, file, disk, arch).
/// Chỉ non-null khi <paramref name="Ok"/> false; nêu rõ cổng đã fail (host, file, đĩa, arch).
/// </param>
/// <param name="UvExePresent">
/// Whether the bundled uv.exe exists on disk — later wizard steps skip with it (spec §4).
/// uv.exe đi kèm có tồn tại trên đĩa không — các bước sau của wizard skip nhờ cờ này (spec §4).
/// </param>
/// <param name="VenvPresent">
/// Whether the sidecar venv python already exists — the sync step can skip (spec §4).
/// Python của venv sidecar đã có chưa — bước sync có thể skip (spec §4).
/// </param>
/// <param name="CudaWheel">
/// "cu130" | "cu129" | "cu126" when <paramref name="Flavor"/> is GPU; null when CPU.
/// "cu130" | "cu129" | "cu126" khi <paramref name="Flavor"/> là GPU; null khi CPU.
/// </param>
public sealed record SystemCheckResult(
    bool Ok,
    OcrFlavor Flavor,
    string? Notice,
    string? FailureReason,
    bool UvExePresent,
    bool VenvPresent,
    string? CudaWheel);

/// <summary>
/// First-run system check (spec §4 step 1): picks the OCR wheel flavor — and for NVIDIA the highest
/// paddlepaddle-gpu CUDA variant the driver fully supports — and fails the wizard outright on hard
/// gates: non-x64 OS, missing uv.exe, disk below the per-flavor floor, unreachable network-probe host.
/// Pure: every side effect (adapter source, disk query, HTTPS probe, file existence) is injected, so
/// the decision is unit-testable with fakes.
/// Check system lần đầu chạy (spec §4 bước 1): chọn flavor wheel OCR — và với NVIDIA là biến thể
/// paddlepaddle-gpu CUDA cao nhất mà driver hỗ trợ đầy đủ — và fail cứng wizard khi phạm cổng: OS
/// non-x64, thiếu uv.exe, đĩa dưới ngưỡng theo flavor, host probe không tới được. Thuần khiết: mọi
/// side effect (nguồn adapter, hỏi đĩa, probe HTTPS, kiểm tra file) đều được inject nên decision
/// test được bằng fake.
/// </summary>
public static class SystemCheck
{
    /// <summary>
    /// Free-disk floor for the GPU flavor (~wheel + venv + models headroom, spec §5.7) — recalibrate
    /// with measured `uv sync` sizes (spec §10).
    /// Ngưỡng đĩa trống cho flavor GPU (đệm wheel + venv + model, spec §5.7) — hiệu chỉnh bằng số đo
    /// `uv sync` thật (spec §10).
    /// </summary>
    public const long MinFreeBytesGpu = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// Free-disk floor for the CPU flavor (spec §5.7) — recalibrate with measured sizes (spec §10).
    /// Ngưỡng đĩa trống cho flavor CPU (spec §5.7) — hiệu chỉnh bằng số đo thật (spec §10).
    /// </summary>
    public const long MinFreeBytesCpu = 5L * 1024 * 1024 * 1024;

    /// <summary>
    /// Hosts setup must reach over HTTPS: PyPI, the PaddlePaddle wheel index, and GitHub (uv downloads
    /// its managed CPython from python-build-standalone releases there, spec §5.7).
    /// Các host setup phải tới được qua HTTPS: PyPI, index wheel PaddlePaddle, và GitHub (uv tải
    /// CPython managed từ python-build-standalone releases ở đó, spec §5.7).
    /// </summary>
    public static readonly string[] NetworkProbeHosts = { "pypi.org", "www.paddlepaddle.org.cn", "github.com" };
    // Host model của paddlex xác nhận khi implement (spec §10) — thêm vào array lúc đó.

    // Bậc thang wheel paddlepaddle-gpu 3.4.x (chỉ có wheel CUDA 12.6/12.9/13.0 — không có 12.7/12.8),
    // chọn wheel CAO NHẤT mà driver hỗ trợ ĐẦY ĐỦ. Nguồn: CUDA Toolkit Release Notes — Table 3 (GA
    // Windows x86_64 minima: 12.6 GA >= 560.76, 12.9 GA >= 576.02) và Table 2 (13.x minor-version
    // compatibility: Min >= 580; cột Windows của 13.0 GA trong Table 3 là N/A vì display driver không
    // còn bundle từ 13.0 → sàn Windows thực dụng là nhánh 580). Ngưỡng mã hóa major*100+minor.
    //
    // Ladder floors for paddlepaddle-gpu 3.4.x wheels (CUDA 12.6/12.9/13.0 only — no 12.7/12.8 wheels):
    // pick the HIGHEST wheel the driver FULLY supports. Source: CUDA Toolkit Release Notes — Table 3
    // (GA Windows x86_64 minima: 12.6 GA >= 560.76, 12.9 GA >= 576.02) and Table 2 (13.x minor-version
    // compatibility: Min >= 580; the 13.0 GA Table 3 Windows column is N/A because the display driver
    // is no longer bundled from 13.0 → the 580 branch floor is the operative Windows number). Floors are
    // encoded as major * 100 + minor.
    private const int MinDriverForCu130 = 58_000; // 580.00
    private const int MinDriverForCu129 = 57_602; // 576.02 — CUDA 12.9 GA
    private const int MinDriverForCu126 = 56_076; // 560.76 — CUDA 12.6 GA

    private const long BytesPerGb = 1024L * 1024 * 1024;

    private static bool NameContains(string name, string token) =>
        name.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the check gates in order: x64 OS → uv.exe present → flavor + CUDA wheel from the adapters
    /// (NVIDIA ladder wins over AMD/iGPU notices) → disk floor for that flavor → an HTTPS probe of every
    /// <see cref="NetworkProbeHosts"/> entry. The arch gate reads
    /// <see cref="Environment.Is64BitOperatingSystem"/> directly (not injectable).
    /// Chạy các cổng check theo thứ tự: OS x64 → uv.exe có mặt → flavor + wheel CUDA từ adapter (bậc
    /// thang NVIDIA thắng notice AMD/iGPU) → ngưỡng đĩa theo flavor đó → probe HTTPS từng host trong
    /// <see cref="NetworkProbeHosts"/>. Cổng arch đọc trực tiếp
    /// <see cref="Environment.Is64BitOperatingSystem"/> (không inject được).
    /// </summary>
    /// <param name="adapters">
    /// Display-adapter inventory. / Danh sách display adapter.
    /// </param>
    /// <param name="diskFreeBytes">
    /// Free bytes on the volume containing the given path; called once with <see cref="SetupPaths.AppDir"/>.
    /// Số byte trống trên ổ chứa path được đưa; gọi một lần với <see cref="SetupPaths.AppDir"/>.
    /// </param>
    /// <param name="isHostReachable">
    /// Short-timeout HTTPS reachability probe per host. / Probe HTTPS tới được hay không, timeout ngắn, theo từng host.
    /// </param>
    /// <param name="fileExists">
    /// File-existence probe for uv.exe and the venv python; defaults to <see cref="File.Exists"/>;
    /// injectable so the uv gate is unit-testable (the real uv.exe is copied into the test output too).
    /// Probe tồn tại file cho uv.exe và python của venv; mặc định là <see cref="File.Exists"/>; inject
    /// được để cổng uv unit-test được (uv.exe thật cũng được copy vào output của test).
    /// </param>
    /// <returns>
    /// The decision (Flavor, CudaWheel, Notice) when every gate passes; otherwise Ok=false with
    /// FailureReason naming the failed gate.
    /// Quyết định (Flavor, CudaWheel, Notice) khi mọi cổng đạt; ngược lại Ok=false kèm FailureReason
    /// nêu rõ cổng fail.
    /// </returns>
    public static SystemCheckResult Run(
        IVideoAdapterSource adapters,
        Func<string, long> diskFreeBytes,
        Func<string, bool> isHostReachable,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(diskFreeBytes);
        ArgumentNullException.ThrowIfNull(isHostReachable);
        fileExists ??= File.Exists;

        bool uvExePresent = fileExists(SetupPaths.UvExe);
        bool venvPresent = fileExists(SetupPaths.VenvPythonExe);

        if (!Environment.Is64BitOperatingSystem)
        {
            return Fail(OcrFlavor.Cpu,
                "ReTran yêu cầu Windows 64-bit (x64); máy này đang chạy Windows 32-bit.",
                uvExePresent, venvPresent);
        }

        if (!uvExePresent)
        {
            return Fail(OcrFlavor.Cpu,
                $"Thiếu uv.exe trong thư mục app ({SetupPaths.UvExe}) — hãy giải nén lại zip đầy đủ.",
                uvExePresent, venvPresent);
        }

        OcrFlavor flavor;
        string? notice;
        string? cudaWheel;
        (flavor, notice, cudaWheel) = DecideFlavor(adapters.GetAdapters());

        long minimumFree = flavor == OcrFlavor.Gpu ? MinFreeBytesGpu : MinFreeBytesCpu;
        long freeBytes = diskFreeBytes(SetupPaths.AppDir);
        if (freeBytes < minimumFree)
        {
            string flavorLabel = flavor == OcrFlavor.Gpu ? "GPU" : "CPU";
            return Fail(flavor,
                $"Không đủ dung lượng đĩa trên ổ chứa thư mục app: bản {flavorLabel} cần tối thiểu " +
                $"{minimumFree / BytesPerGb} GB, hiện chỉ còn {freeBytes / (double)BytesPerGb:0.##} GB.",
                uvExePresent, venvPresent);
        }

        foreach (string host in NetworkProbeHosts)
        {
            if (!isHostReachable(host))
            {
                return Fail(flavor,
                    $"Không kết nối được tới host '{host}' — setup cần mạng để tải Python, wheel paddle " +
                    "và model; kiểm tra mạng/proxy rồi chạy lại check.",
                    uvExePresent, venvPresent);
            }
        }

        return new SystemCheckResult(true, flavor, notice, null, uvExePresent, venvPresent, cudaWheel);
    }

    private static SystemCheckResult Fail(OcrFlavor flavor, string reason, bool uvExePresent, bool venvPresent)
        => new(false, flavor, null, reason, uvExePresent, venvPresent, CudaWheel: null);

    /// <summary>
    /// Flavor decision (spec §3.5, §5.7): the highest CUDA wheel fully supported by any NVIDIA adapter's
    /// driver → GPU flavor with that wheel; NVIDIA present but no adapter qualifies (old or unreadable
    /// driver) → CPU + "driver chưa đủ mới" notice; discrete AMD → CPU + the spec's AMD notice; nothing
    /// else matches → CPU + a light notice that never says "card AMD".
    /// Quyết định flavor (spec §3.5, §5.7): wheel CUDA cao nhất được hỗ trợ ĐẦY ĐỦ bởi driver của bất kỳ
    /// adapter NVIDIA nào → GPU flavor kèm wheel đó; có NVIDIA nhưng không adapter nào đạt (driver cũ
    /// hoặc không đọc được) → CPU + notice "driver chưa đủ mới"; AMD rời → CPU + notice AMD đúng spec;
    /// còn lại → CPU + notice nhẹ, tuyệt đối không nhắc "card AMD".
    /// </summary>
    private static (OcrFlavor Flavor, string? Notice, string? CudaWheel) DecideFlavor(
        IReadOnlyList<VideoAdapterInfo> adapters)
    {
        bool nvidiaPresent = false;
        bool amdDiscrete = false;
        string? bestWheel = null;

        foreach (VideoAdapterInfo adapter in adapters)
        {
            if (NameContains(adapter.Name, "NVIDIA"))
            {
                nvidiaPresent = true;
                string? wheel = CudaWheelForAdapter(adapter);
                // Optimus/nhiều card NVIDIA: giữ wheel cao nhất (so sánh ordinal "cu126" < "cu129" < "cu130").
                if (string.CompareOrdinal(bestWheel, wheel) < 0)
                {
                    bestWheel = wheel;
                }
            }
            else if (adapter.IsDiscrete && (NameContains(adapter.Name, "Radeon") || NameContains(adapter.Name, "AMD")))
            {
                amdDiscrete = true;
            }
        }

        if (bestWheel is not null)
        {
            return (OcrFlavor.Gpu, null, bestWheel);
        }

        if (nvidiaPresent)
        {
            return (OcrFlavor.Cpu,
                $"Driver NVIDIA hiện tại chưa đủ mới để dùng bản GPU (cần tối thiểu {FormatMinDriver()} " +
                "cho CUDA 12.x) — wizard sẽ cài bản CPU.",
                null);
        }

        if (amdDiscrete)
        {
            return (OcrFlavor.Cpu,
                "Không cài được bản GPU cho card AMD trên Windows, sẽ thay bằng bản CPU.",
                null);
        }

        return (OcrFlavor.Cpu, "Không có GPU NVIDIA hỗ trợ — wizard sẽ dùng bản CPU.", null);
    }

    private static string? CudaWheelForAdapter(VideoAdapterInfo adapter)
        => TryGetNvidiaProductVersion(adapter.DriverVersion, out var productVersion)
            ? CudaWheelForDriverVersion(productVersion.Major, productVersion.Minor)
            : null;

    /// <summary>
    /// Maps an NVIDIA Windows product driver version (major + two-digit minor, e.g. 576 and 02 for
    /// 576.02) to the highest paddlepaddle-gpu 3.4.x CUDA wheel variant the driver FULLY supports:
    /// ≥ 580.00 → "cu130", ≥ 576.02 → "cu129", ≥ 560.76 → "cu126", below 560.76 or an out-of-format
    /// minor → null (CPU flavor). Floors: CUDA Toolkit Release Notes Table 3 (12.6 GA Windows x86_64
    /// ≥ 560.76, 12.9 GA ≥ 576.02) and Table 2 (13.x min ≥ 580 — the 13.0 GA Windows column is N/A
    /// since 13.0 no longer bundles a display driver, so the 580 branch floor is operative). Pure.
    /// Ánh xạ product driver version NVIDIA trên Windows (major + minor đúng 2 chữ số, vd 576 và 02
    /// cho 576.02) sang biến thể wheel paddlepaddle-gpu 3.4.x CUDA CAO NHẤT mà driver hỗ trợ ĐẦY ĐỦ:
    /// ≥ 580.00 → "cu130", ≥ 576.02 → "cu129", ≥ 560.76 → "cu126", dưới 560.76 hoặc minor sai format →
    /// null (flavor CPU). Ngưỡng: CUDA Toolkit Release Notes Table 3 (12.6 GA Windows x86_64 ≥ 560.76,
    /// 12.9 GA ≥ 576.02) và Table 2 (13.x min ≥ 580 — cột Windows của 13.0 GA là N/A vì 13.0 không còn
    /// bundle display driver, sàn thực dụng là nhánh 580). Hàm thuần.
    /// </summary>
    /// <param name="productMajor">
    /// Driver major, e.g. 576. / Major của driver, vd 576.
    /// </param>
    /// <param name="productMinor">
    /// Driver minor, always two digits (0..99); out-of-range values map to null.
    /// Minor của driver, luôn 2 chữ số (0..99); ngoài khoảng → null.
    /// </param>
    /// <returns>
    /// "cu130" | "cu129" | "cu126", or null when no CUDA wheel is fully supported.
    /// "cu130" | "cu129" | "cu126", hoặc null khi không wheel CUDA nào được hỗ trợ đầy đủ.
    /// </returns>
    public static string? CudaWheelForDriverVersion(int productMajor, int productMinor)
    {
        if (productMinor is < 0 or > 99)
        {
            return null;
        }

        int version = productMajor * 100 + productMinor;
        return version switch
        {
            >= MinDriverForCu130 => "cu130",
            >= MinDriverForCu129 => "cu129",
            >= MinDriverForCu126 => "cu126",
            _ => null,
        };
    }

    /// <summary>
    /// Maps NVIDIA's WMI/D3D driver version ("NN.0.B.TTTT") to NVIDIA's product-version space
    /// (major, two-digit minor): D3D major 30/31/32 cover the product 4xx/5xx/6xx series, and TTTT
    /// encodes (productMajor % 100) * 100 + productMinor. Verified against the dev machine on
    /// 2026-11-05: WMI "32.0.16.1742" == nvidia-smi driver_version 617.42; note the brief's "nvdrv.dll"
    /// does not exist on modern NVIDIA installs, so WMI is the source. Returns false for null,
    /// non-numeric, or D3D majors outside 30–32 (pre-CUDA-12 branches) — callers treat that as "no
    /// CUDA support", conservatively choosing CPU with a notice.
    /// Ánh xạ driver version NVIDIA dạng WMI/D3D ("NN.0.B.TTTT") sang không gian product version của
    /// NVIDIA (major, minor đúng 2 chữ số): D3D major 30/31/32 tương ứng seri sản phẩm 4xx/5xx/6xx, còn
    /// TTTT mã hóa (productMajor % 100) * 100 + productMinor. Đã kiểm chứng trên máy dev ngày
    /// 2026-11-05: WMI "32.0.16.1742" == nvidia-smi driver_version 617.42; lưu ý file "nvdrv.dll" mà
    /// brief nhắc KHÔNG tồn tại trên bản cài NVIDIA hiện đại, nên WMI là nguồn. Trả false khi null,
    /// không phải số, hoặc D3D major ngoài khoảng 30–32 (họ tiền CUDA 12) — caller coi là "không hỗ trợ
    /// CUDA", an toàn chọn CPU kèm notice.
    /// </summary>
    /// <param name="wmiDriverVersion">
    /// Raw WMI DriverVersion string. / Chuỗi WMI DriverVersion thô.
    /// </param>
    /// <param name="productVersion">
    /// Parsed (major, minor 0..99) when mapping succeeded. / (major, minor 0..99) khi ánh xạ thành công.
    /// </param>
    internal static bool TryGetNvidiaProductVersion(
        string? wmiDriverVersion,
        out (int Major, int Minor) productVersion)
    {
        productVersion = default;
        if (wmiDriverVersion is null)
        {
            return false;
        }

        string[] segments = wmiDriverVersion.Split('.');
        if (segments.Length < 4)
        {
            return false;
        }

        if (!int.TryParse(segments[0], out int d3dMajor) || !int.TryParse(segments[^1], out int tail))
        {
            return false;
        }

        int productBase = d3dMajor switch { 30 => 400, 31 => 500, 32 => 600, _ => -1 };
        if (productBase < 0 || tail is < 0 or > 9999)
        {
            return false;
        }

        productVersion = (productBase + tail / 100, tail % 100);
        return true;
    }

    /// <summary>
    /// Formats the CUDA 12.6 GA driver floor (56_076) as the user-facing "560.76" minimum quoted in the
    /// CPU-downgrade notice. / Định dạng ngưỡng driver 56_076 thành "560.76" hiển thị cho user trong notice hạ CPU.
    /// </summary>
    private static string FormatMinDriver()
        => $"{MinDriverForCu126 / 100}.{MinDriverForCu126 % 100:00}";
}
