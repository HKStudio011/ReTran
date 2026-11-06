using System.Runtime.InteropServices;

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
/// raw form the source supplies it (WMI gives NVIDIA's INF form "DDI.0.BRANCH.TTTT", where the BRANCH
/// segment carries the product family — see
/// <see cref="SystemCheck.TryGetNvidiaProductVersion"/> for the mapping to NVIDIA's product-version form).
/// Một display adapter hệ thống nhìn thấy: tên hiển thị, cờ card rời, phiên bản driver ở dạng thô mà
/// nguồn cung cấp (WMI trả dạng INF của NVIDIA "DDI.0.BRANCH.TTTT", trong đó segment BRANCH mang họ sản
/// phẩm — xem <see cref="SystemCheck.TryGetNvidiaProductVersion"/> để biết cách ánh xạ về dạng product version).
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
    /// its managed CPython from python-build-standalone releases there, spec §5.7). Published as a
    /// read-only view so callers cannot mutate the shared probe list.
    /// Các host setup phải tới được qua HTTPS: PyPI, index wheel PaddlePaddle, và GitHub (uv tải
    /// CPython managed từ python-build-standalone releases ở đó, spec §5.7). Publish dưới dạng read-only
    /// view để caller không sửa được danh sách probe chia sẻ chung.
    /// </summary>
    public static IReadOnlyList<string> NetworkProbeHosts { get; } =
        ["pypi.org", "www.paddlepaddle.org.cn", "github.com"];
    // Host model của paddlex xác nhận khi implement (spec §10) — thêm vào danh sách này lúc đó.

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

    // Hạng wheel khi phải chọn giữa nhiều adapter NVIDIA: theo SỐ PHIÊN BẢN CUDA (12.6 → 126 …), không so
    // sánh ordinal nhãn "cuXXX" — nhãn ngắn hơn ("cu99") hay dài hơn ("cu10x") đều làm phép so sánh chuỗi
    // sai. Thêm nấc mới: thêm một nhãn + một hạng, không phụ thuộc cách đặt tên.
    // Wheel rank when several NVIDIA adapters compete: by CUDA VERSION NUMBER (12.6 → 126 …), never by
    // string.CompareOrdinal on the "cuXXX" label — a shorter ("cu99") or longer ("cu10x") label breaks
    // ordinal comparison. Adding a rung: add a label plus a rank, independent of the naming scheme.
    private const int Cuda130Rank = 130; // "cu130"
    private const int Cuda129Rank = 129; // "cu129"
    private const int Cuda126Rank = 126; // "cu126"

    private const long BytesPerGb = 1024L * 1024 * 1024;

    private static bool NameContains(string name, string token) =>
        name.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the check gates in order: x64 OS → uv.exe present → flavor + CUDA wheel from the adapters
    /// (NVIDIA ladder wins over AMD/iGPU notices) → disk floor for that flavor → an HTTPS probe of every
    /// <see cref="NetworkProbeHosts"/> entry. The arch gate reads <see cref="RuntimeInformation.OSArchitecture"/>
    /// through the injectable <paramref name="osArchitecture"/> seam (same pattern as
    /// <paramref name="fileExists"/>): the app ships an x64-only uv.exe and x64-only paddle wheels, so an
    /// ARM64 Windows machine — which runs Win32 code fine via emulation — must still be sent to the CPU
    /// path / fail the gate rather than assume a 32-bit OS.
    /// Chạy các cổng check theo thứ tự: OS x64 → uv.exe có mặt → flavor + wheel CUDA từ adapter (bậc
    /// thang NVIDIA thắng notice AMD/iGPU) → ngưỡng đĩa theo flavor đó → probe HTTPS từng host trong
    /// <see cref="NetworkProbeHosts"/>. Cổng arch đọc <see cref="RuntimeInformation.OSArchitecture"/> qua
    /// seam inject được <paramref name="osArchitecture"/> (cùng pattern với <paramref name="fileExists"/>):
    /// app phát hành uv.exe chỉ bản x64 và wheel paddle chỉ bản x64, nên máy Windows ARM64 — vẫn chạy code
    /// Win32 bình thường nhờ emulation — vẫn phải bị cổng arch chặn, thay vì bị gán là "Windows 32-bit".
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
    /// <param name="osArchitecture">
    /// OS architecture the arch gate tests against <see cref="Architecture.X64"/>; defaults to
    /// <see cref="RuntimeInformation.OSArchitecture"/> and is injectable so the ARM64/X86 branch is
    /// unit-testable. Optional, so every existing call site keeps compiling.
    /// Kiến trúc OS mà cổng arch so với <see cref="Architecture.X64"/>; mặc định là
    /// <see cref="RuntimeInformation.OSArchitecture"/> và inject được để nhánh ARM64/X86 unit-test được.
    /// Optional nên mọi call site đang có vẫn compile.
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
        Func<string, bool>? fileExists = null,
        Func<Architecture>? osArchitecture = null)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(diskFreeBytes);
        ArgumentNullException.ThrowIfNull(isHostReachable);
        fileExists ??= File.Exists;
        osArchitecture ??= static () => RuntimeInformation.OSArchitecture;

        bool uvExePresent = fileExists(SetupPaths.UvExe);
        bool venvPresent = fileExists(SetupPaths.VenvPythonExe);

        // Không dùng Environment.Is64BitOperatingSystem: máy ARM64 trả true (Win32-on-ARM) nhưng uv.exe
        // đi kèm + wheel paddle chỉ có bản x64. Thông báo cũng không được nói "Windows 32-bit".
        // Environment.Is64BitOperatingSystem is deliberately not used: an ARM64 machine reports true
        // (Win32-on-ARM) while the bundled uv.exe and the paddle wheels are x64-only. The message must
        // not claim "Windows 32-bit" either.
        Architecture architecture = osArchitecture();
        if (architecture != Architecture.X64)
        {
            return Fail(OcrFlavor.Cpu,
                $"ReTran yêu cầu Windows chạy trên nền x64; kiến trúc OS của máy này là '{architecture}' — " +
                "uv.exe đi kèm và wheel paddle chỉ có bản x64.",
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
    /// Flavor decision (spec §3.5, §5.7): the highest CUDA wheel fully supported by the driver of any
    /// DISCRETE NVIDIA adapter → GPU flavor with that wheel; a discrete NVIDIA adapter present but none
    /// qualifies (old or unreadable driver) → CPU + "driver chưa đủ mới" notice; discrete AMD → CPU + the
    /// spec's AMD notice; nothing else matches → CPU + a light notice that never says "card AMD".
    /// Quyết định flavor (spec §3.5, §5.7): wheel CUDA cao nhất được hỗ trợ ĐẦY ĐỦ bởi driver của bất kỳ
    /// adapter NVIDIA RỜI nào → GPU flavor kèm wheel đó; có NVIDIA rời nhưng không adapter nào đạt (driver
    /// cũ hoặc không đọc được) → CPU + notice "driver chưa đủ mới"; AMD rời → CPU + notice AMD đúng spec;
    /// còn lại → CPU + notice nhẹ, tuyệt đối không nhắc "card AMD".
    /// </summary>
    private static (OcrFlavor Flavor, string? Notice, string? CudaWheel) DecideFlavor(
        IReadOnlyList<VideoAdapterInfo> adapters)
    {
        bool nvidiaDiscretePresent = false;
        bool amdDiscrete = false;
        string? bestWheel = null;
        int bestWheelRank = 0;

        foreach (VideoAdapterInfo adapter in adapters)
        {
            // Spec §3.5 chỉ xét NVIDIA RỜI, đối xứng với nhánh AMD bên dưới: adapter iGPU đời cũ hoặc
            // adapter ảo đặt tên "NVIDIA …" không được chọn flavor GPU. GIỚI HẠN đã biết: IsDiscrete đến
            // từ heuristic đặt tên VideoAdapterSource.IsDiscreteName, nên adapter data-center/pro không
            // mang token GeForce/Quadro/Tesla/NVS (vd "NVIDIA T4") bị coi là không rời và rớt xuống CPU —
            // hướng fail-safe (không bao giờ cài wheel mà driver máy đó không chạy nổi).
            // Spec §3.5 considers a DISCRETE NVIDIA only, symmetric with the AMD branch below: an old iGPU
            // or a virtual adapter named "NVIDIA …" must not pick the GPU flavor. KNOWN LIMITATION:
            // IsDiscrete comes from the name heuristic VideoAdapterSource.IsDiscreteName, so a
            // data-center/pro adapter without a GeForce/Quadro/Tesla/NVS token (e.g. "NVIDIA T4") counts
            // as non-discrete and falls back to CPU — the fail-safe direction (it never installs a wheel
            // that machine's driver cannot run).
            if (adapter.IsDiscrete && NameContains(adapter.Name, "NVIDIA"))
            {
                nvidiaDiscretePresent = true;
                string? wheel = CudaWheelForAdapter(adapter);
                int rank = WheelRank(wheel);
                // Optimus/nhiều card NVIDIA: giữ wheel có hạng CAO NHẤT (rank số nguyên, không so nhãn).
                // Optimus / several NVIDIA cards: keep the HIGHEST-ranked wheel (integer rank, not labels).
                if (rank > bestWheelRank)
                {
                    bestWheel = wheel;
                    bestWheelRank = rank;
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

        if (nvidiaDiscretePresent)
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
    /// Integer rank of a CUDA wheel label, used to pick the best wheel across several NVIDIA adapters
    /// (R43): rank = the CUDA version number ("cu130" → 130, "cu129" → 129, "cu126" → 126), so ordering
    /// never depends on how the label string compares. Must stay in sync with the labels returned by
    /// <see cref="CudaWheelForDriverVersion"/>; null or an unlisted label ranks 0 and never wins.
    /// Hạng nguyên của nhãn wheel CUDA, dùng để chọn wheel tốt nhất giữa nhiều adapter NVIDIA (R43):
    /// rank = số phiên bản CUDA ("cu130" → 130, "cu129" → 129, "cu126" → 126), nên thứ tự không bao giờ
    /// phụ thuộc cách so sánh chuỗi nhãn. Phải khớp với các nhãn mà <see cref="CudaWheelForDriverVersion"/>
    /// trả về; null hoặc nhãn chưa khai báo có hạng 0 và không bao giờ thắng.
    /// </summary>
    private static int WheelRank(string? wheel) => wheel switch
    {
        "cu130" => Cuda130Rank,
        "cu129" => Cuda129Rank,
        "cu126" => Cuda126Rank,
        _ => 0,
    };

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
    /// Maps NVIDIA's WMI/INF driver version ("DDI.0.BRANCH.TTTT") to NVIDIA's product-version space
    /// (major, two-digit minor). The PRODUCT FAMILY comes from the third segment — the driver branch:
    /// 14 → 4xx, 15 → 5xx, 16 → 6xx — while TTTT encodes (productMajor % 100) * 100 + productMinor.
    /// The first segment (DDI/INF major) carries NO product information and must never gate the
    /// decision: branch 15 ships under DDI major 31 ("31.0.15.3699" = 536.99) AND under DDI major 32
    /// ("32.0.15.6094" = 560.94), and branch 14 ships under DDI major 27 ("27.21.14.5609" = 456.09).
    /// Measured on the dev machine: 2026-11-06 WMI "32.0.16.2114" ↔ nvidia-smi driver_version 621.14 and
    /// 2026-11-05 WMI "32.0.16.1742" ↔ nvidia-smi 617.42 — the hundreds digit follows the 16 branch, not
    /// the DDI major. Deriving the base from the DDI major (the behaviour before Task 10) turned 560.94
    /// into a non-existent 660.94 and made the wizard install cu130 on a machine whose driver only
    /// reaches cu126. Any other branch (e.g. 13 or 17) is refused, as are null, non-numeric and
    /// out-of-guard tails — callers treat that as "no CUDA support" and conservatively choose CPU with a
    /// notice. "nvdrv.dll", named by the old brief, does not exist on modern NVIDIA installs, so WMI is
    /// the source.
    /// Ánh xạ driver version NVIDIA dạng WMI/INF ("DDI.0.BRANCH.TTTT") sang không gian product version
    /// của NVIDIA (major, minor đúng 2 chữ số). HỌ SẢN PHẨM đến từ segment thứ ba — branch driver:
    /// 14 ⇒ 4xx, 15 ⇒ 5xx, 16 ⇒ 6xx — còn TTTT mã hóa (productMajor % 100) * 100 + productMinor. Segment
    /// đầu (DDI/INF major) KHÔNG mang thông tin sản phẩm và không bao giờ được gate: branch 15 chạy dưới
    /// DDI major 31 ("31.0.15.3699" = 536.99) LẪN 32 ("32.0.15.6094" = 560.94), còn branch 14 chạy dưới
    /// DDI major 27 ("27.21.14.5609" = 456.09). Đo trên máy dev: 2026-11-06 WMI "32.0.16.2114" ↔
    /// nvidia-smi driver_version 621.14 và 2026-11-05 WMI "32.0.16.1742" ↔ nvidia-smi 617.42 — số trăm đi
    /// theo branch 16 chứ không theo DDI major. Suy base từ DDI major (cách làm trước Task 10) biến 560.94
    /// thành 660.94 không tồn tại và khiến wizard cài cu130 trên máy mà driver chỉ tới nổi cu126. Mọi
    /// branch khác (vd 13 hay 17) bị từ chối, cùng với null, chuỗi không phải số và tail ngoài guard —
    /// caller coi là "không hỗ trợ CUDA", an toàn chọn CPU kèm notice. File "nvdrv.dll" brief cũ nhắc
    /// KHÔNG tồn tại trên bản cài NVIDIA hiện đại, nên WMI là nguồn.
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

        // segments[0] (DDI major) chỉ được parse như guard format — nó không còn là nguồn số trăm.
        // segments[0] (DDI major) is parsed only as a format guard — it is no longer the product base.
        if (!int.TryParse(segments[0], out _) || !int.TryParse(segments[2], out int branch)
            || !int.TryParse(segments[^1], out int tail))
        {
            return false;
        }

        // Số trăm của product version nằm ở BRANCH (segment thứ ba), không phải DDI major: branch 15
        // chạy dưới cả DDI 31 ("31.0.15.3699" = 536.99) lẫn 32 ("32.0.15.6094" = 560.94), branch 14 chạy
        // dưới DDI 27 ("27.21.14.5609" = 456.09). Xem doc comment ở trên cho chứng cứ đo thật.
        // The product version's hundreds digit comes from the BRANCH (third segment), never the DDI
        // major: branch 15 ships under DDI 31 ("31.0.15.3699" = 536.99) AND 32 ("32.0.15.6094" = 560.94),
        // and branch 14 ships under DDI 27 ("27.21.14.5609" = 456.09). Measured datapoints are in the
        // doc comment above.
        int productBase = branch switch { 14 => 400, 15 => 500, 16 => 600, _ => -1 };
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
