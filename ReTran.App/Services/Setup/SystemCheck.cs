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
/// Advisory message to show the user when the decision downgraded to CPU (driver/AMD/no-NVIDIA); null on
/// GPU. Carries an i18n key the UI resolves in the current language.
/// Thông báo advisory cho người dùng khi quyết định hạ xuống CPU (driver/AMD/không NVIDIA); null khi GPU.
/// Mang key i18n để UI resolve theo ngôn ngữ hiện hành.
/// </param>
/// <param name="FailureReason">
/// Non-null only when <paramref name="Ok"/> is false; a localizable message naming the failed gate
/// (host, file, disk, arch). Chỉ non-null khi <paramref name="Ok"/> false; thông điệp bản địa hoá nêu
/// rõ cổng đã fail (host, file, đĩa, arch).
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
/// "cu129" — the only paddlepaddle-gpu 3.4.x CUDA wheel the wizard installs — when
/// <paramref name="Flavor"/> is GPU; null when CPU.
/// "cu129" — wheel paddlepaddle-gpu 3.4.x CUDA duy nhất mà wizard cài — khi <paramref name="Flavor"/>
/// là GPU; null khi CPU.
/// </param>
public sealed record SystemCheckResult(
    bool Ok,
    OcrFlavor Flavor,
    SetupMessage? Notice,
    SetupMessage? FailureReason,
    bool UvExePresent,
    bool VenvPresent,
    string? CudaWheel);

/// <summary>
/// First-run system check (spec §4 step 1): picks the OCR wheel flavor — for NVIDIA, the GPU flavor
/// with the single supported paddlepaddle-gpu CUDA wheel ("cu129") when the driver reaches the
/// 576.02 floor, otherwise CPU with a notice — and fails the wizard outright on hard gates: non-x64
/// OS, missing uv.exe, disk below the per-flavor floor, unreachable network-probe host. Pure: every
/// side effect (adapter source, disk query, HTTPS probe, file existence) is injected, so the decision
/// is unit-testable with fakes.
/// Check system lần đầu chạy (spec §4 bước 1): chọn flavor wheel OCR — với NVIDIA là flavor GPU kèm
/// wheel paddlepaddle-gpu CUDA duy nhất ("cu129") khi driver đạt sàn 576.02, không thì CPU kèm notice
/// — và fail cứng wizard khi phạm cổng: OS non-x64, thiếu uv.exe, đĩa dưới ngưỡng theo flavor, host
/// probe không tới được. Thuần khiết: mọi side effect (nguồn adapter, hỏi đĩa, probe HTTPS, kiểm tra
/// file) đều được inject nên decision test được bằng fake.
/// </summary>
public static class SystemCheck
{
    // Số đo thật 2026-11-07 (Task 11b): venv GPU sau `uv sync` = 3,96 GB — sàn 8 GB vẫn dư cho model.
    // Measured 2026-11-07 (Task 11b): GPU venv after `uv sync` = 3.96 GB — the 8 GB floor still leaves
    // headroom for models.
    /// <summary>
    /// Free-disk floor for the GPU flavor (~wheel + venv + models headroom, spec §5.7) — recalibrate
    /// with measured `uv sync` sizes (spec §10).
    /// Ngưỡng đĩa trống cho flavor GPU (đệm wheel + venv + model, spec §5.7) — hiệu chỉnh bằng số đo
    /// `uv sync` thật (spec §10).
    /// </summary>
    public const long MinFreeBytesGpu = 8L * 1024 * 1024 * 1024;

    // Số đo thật 2026-11-07 (Task 11b): venv CPU sau `uv sync` = 0,67 GB, wheel paddle CPU ~105,5 MB
    // (đo 100,7 MiB) — sàn hạ từ 5 GB xuống 2 GB vẫn dư nhiều. Measured 2026-11-07 (Task 11b): CPU venv
    // after `uv sync` = 0.67 GB, paddle CPU wheel ~105.5 MB (measured 100.7 MiB) — the floor drops from
    // 5 GB to 2 GB with plenty of headroom.
    /// <summary>
    /// Free-disk floor for the CPU flavor (spec §5.7) — recalibrate with measured sizes (spec §10).
    /// Ngưỡng đĩa trống cho flavor CPU (spec §5.7) — hiệu chỉnh bằng số đo thật (spec §10).
    /// </summary>
    public const long MinFreeBytesCpu = 2L * 1024 * 1024 * 1024;

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

    // Sàn driver duy nhất cho wheel paddlepaddle-gpu 3.4.x còn lại: CUDA 12.9 GA yêu cầu Windows x86_64
    // >= 576.02 (CUDA Toolkit Release Notes Table 3). Ngưỡng mã hóa major*100+minor. Các nấc cũ bỏ sau
    // đo thật 2026-11-07: index paddle cu126 KHÔNG có wheel 3.4.0 cho cp313/win_amd64 (tối đa 3.3.1) nên
    // `uv sync` với pin ==3.4.0 không resolve được; nấc cu130 cần họ gói nvidia-*-cu13 khác + driver >= 580.
    // Chỉ còn wheel "cu129".
    //
    // Single driver floor for the only remaining paddlepaddle-gpu 3.4.x CUDA wheel: CUDA 12.9 GA requires
    // Windows x86_64 >= 576.02 (CUDA Toolkit Release Notes Table 3). Floor encoded as major*100+minor. The
    // old rungs were dropped after measuring on 2026-11-07: the paddle cu126 index has NO 3.4.0 wheel for
    // cp313/win_amd64 (max 3.3.1), so `uv sync` with the ==3.4.0 pin cannot resolve it; the cu130 rung
    // needs a different nvidia-*-cu13 package family plus driver >= 580. Only the "cu129" wheel remains.
    private const int MinDriverForCu129 = 57_602; // 576.02 — CUDA 12.9 GA

    // Hạng wheel khi phải chọn giữa nhiều adapter NVIDIA: theo SỐ PHIÊN BẢN CUDA (12.9 → 129), không so
    // sánh ordinal nhãn "cuXXX" — nhãn ngắn hơn ("cu99") hay dài hơn ("cu10x") đều làm phép so sánh chuỗi
    // sai. Giữ hạng nguyên dù mới còn một nấc duy nhất: thêm nấc sau này chỉ cần thêm một nhãn + một hạng,
    // không phụ thuộc cách đặt tên.
    // Wheel rank when several NVIDIA adapters compete: by CUDA VERSION NUMBER (12.9 → 129), never by
    // string.CompareOrdinal on the "cuXXX" label — a shorter ("cu99") or longer ("cu10x") label breaks
    // ordinal comparison. The integer rank stays even with a single rung: adding one later means adding a
    // label plus a rank, independent of the naming scheme.
    private const int Cuda129Rank = 129; // "cu129"

    private const long BytesPerGb = 1024L * 1024 * 1024;

    private static bool NameContains(string name, string token) =>
        name.Contains(token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the check gates in order: x64 OS → uv.exe present → flavor + CUDA wheel from the adapters
    /// (the NVIDIA cu129 floor wins over AMD/iGPU notices) → disk floor for that flavor → an HTTPS probe
    /// of every <see cref="NetworkProbeHosts"/> entry. The arch gate reads <see cref="RuntimeInformation.OSArchitecture"/>
    /// through the injectable <paramref name="osArchitecture"/> seam (same pattern as
    /// <paramref name="fileExists"/>): the app ships an x64-only uv.exe and x64-only paddle wheels, so an
    /// ARM64 Windows machine — which runs Win32 code fine via emulation — must still be sent to the CPU
    /// path / fail the gate rather than assume a 32-bit OS.
    /// Chạy các cổng check theo thứ tự: OS x64 → uv.exe có mặt → flavor + wheel CUDA từ adapter (sàn
    /// cu129 của NVIDIA thắng notice AMD/iGPU) → ngưỡng đĩa theo flavor đó → probe HTTPS từng host trong
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
                SetupMessage.Of("setup.fail.arch", architecture.ToString()),
                uvExePresent, venvPresent);
        }

        if (!uvExePresent)
        {
            return Fail(OcrFlavor.Cpu,
                SetupMessage.Of("setup.fail.uvMissing", SetupPaths.UvExe),
                uvExePresent, venvPresent);
        }

        OcrFlavor flavor;
        SetupMessage? notice;
        string? cudaWheel;
        (flavor, notice, cudaWheel) = DecideFlavor(adapters.GetAdapters());

        long minimumFree = flavor == OcrFlavor.Gpu ? MinFreeBytesGpu : MinFreeBytesCpu;
        long freeBytes = diskFreeBytes(SetupPaths.AppDir);
        if (freeBytes < minimumFree)
        {
            string flavorLabel = flavor == OcrFlavor.Gpu ? "GPU" : "CPU";
            return Fail(flavor,
                SetupMessage.Of("setup.fail.disk",
                    flavorLabel,
                    (minimumFree / BytesPerGb).ToString(),
                    (freeBytes / (double)BytesPerGb).ToString("0.##")),
                uvExePresent, venvPresent);
        }

        foreach (string host in NetworkProbeHosts)
        {
            if (!isHostReachable(host))
            {
                return Fail(flavor,
                    SetupMessage.Of("setup.fail.network", host),
                    uvExePresent, venvPresent);
            }
        }

        return new SystemCheckResult(true, flavor, notice, null, uvExePresent, venvPresent, cudaWheel);
    }

    private static SystemCheckResult Fail(OcrFlavor flavor, SetupMessage reason, bool uvExePresent, bool venvPresent)
        => new(false, flavor, null, reason, uvExePresent, venvPresent, CudaWheel: null);

    /// <summary>
    /// Flavor decision (spec §3.5, §5.7): a DISCRETE NVIDIA adapter whose driver reaches the cu129 floor
    /// (≥ 576.02) → GPU flavor with the "cu129" wheel; a discrete NVIDIA adapter present but none
    /// qualifies (old or unreadable driver) → CPU + the "driver chưa đủ mới" notice that quotes 576.02
    /// and offers updating the driver and re-running Setup for the GPU flavor; discrete AMD → CPU + the
    /// spec's AMD notice; nothing else matches → CPU + a light notice that never says "card AMD".
    /// Quyết định flavor (spec §3.5, §5.7): adapter NVIDIA RỜI có driver đạt sàn cu129 (≥ 576.02) → GPU
    /// flavor kèm wheel "cu129"; có NVIDIA rời nhưng không adapter nào đạt (driver cũ hoặc không đọc được)
    /// → CPU + notice "driver chưa đủ mới" nêu mốc 576.02 và gợi ý cập nhật driver rồi chạy lại Setup để
    /// dùng bản GPU; AMD rời → CPU + notice AMD đúng spec; còn lại → CPU + notice nhẹ, tuyệt đối không
    /// nhắc "card AMD".
    /// </summary>
    private static (OcrFlavor Flavor, SetupMessage? Notice, string? CudaWheel) DecideFlavor(
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
            // Nội dung notice (kèm mốc sàn driver) nằm ở key i18n setup.notice.oldDriver.
            // The notice text (including the driver floor) lives in the i18n key setup.notice.oldDriver.
            return (OcrFlavor.Cpu, SetupMessage.Of("setup.notice.oldDriver"), null);
        }

        if (amdDiscrete)
        {
            return (OcrFlavor.Cpu, SetupMessage.Of("setup.notice.amdCpu"), null);
        }

        return (OcrFlavor.Cpu, SetupMessage.Of("setup.notice.noGpu"), null);
    }

    private static string? CudaWheelForAdapter(VideoAdapterInfo adapter)
        => TryGetNvidiaProductVersion(adapter.DriverVersion, out var productVersion)
            ? CudaWheelForDriverVersion(productVersion.Major, productVersion.Minor)
            : null;

    /// <summary>
    /// Integer rank of a CUDA wheel label, used to pick the best wheel across several NVIDIA adapters
    /// (R43): rank = the CUDA version number ("cu129" → 129), so ordering never depends on how the label
    /// string compares. Must stay in sync with the labels returned by
    /// <see cref="CudaWheelForDriverVersion"/>; null or an unlisted label ranks 0 and never wins.
    /// Hạng nguyên của nhãn wheel CUDA, dùng để chọn wheel tốt nhất giữa nhiều adapter NVIDIA (R43):
    /// rank = số phiên bản CUDA ("cu129" → 129), nên thứ tự không bao giờ phụ thuộc cách so sánh chuỗi
    /// nhãn. Phải khớp với các nhãn mà <see cref="CudaWheelForDriverVersion"/> trả về; null hoặc nhãn chưa
    /// khai báo có hạng 0 và không bao giờ thắng.
    /// </summary>
    private static int WheelRank(string? wheel) => wheel switch
    {
        "cu129" => Cuda129Rank,
        _ => 0,
    };

    /// <summary>
    /// Maps an NVIDIA Windows product driver version (major + two-digit minor, e.g. 576 and 02 for
    /// 576.02) to the paddlepaddle-gpu 3.4.x CUDA wheel variant when the driver FULLY supports it:
    /// ≥ 576.02 → "cu129" (CUDA 12.9 GA floor, CUDA Toolkit Release Notes Table 3), below 576.02 or an
    /// out-of-format minor → null (CPU flavor). The cu130/cu126 rungs were removed after measuring on
    /// 2026-11-07: the paddle cu126 index has no 3.4.0 wheel for cp313/win_amd64 and cu130 needs a
    /// different nvidia package family. Pure.
    /// Ánh xạ product driver version NVIDIA trên Windows (major + minor đúng 2 chữ số, vd 576 và 02
    /// cho 576.02) sang biến thể wheel paddlepaddle-gpu 3.4.x CUDA khi driver hỗ trợ ĐẦY ĐỦ:
    /// ≥ 576.02 → "cu129" (sàn CUDA 12.9 GA, CUDA Toolkit Release Notes Table 3), dưới 576.02 hoặc minor
    /// sai format → null (flavor CPU). Các nấc cu130/cu126 đã bỏ sau đo thật 2026-11-07: index paddle
    /// cu126 không có wheel 3.4.0 cho cp313/win_amd64 và cu130 cần họ gói nvidia khác. Hàm thuần.
    /// </summary>
    /// <param name="productMajor">
    /// Driver major, e.g. 576. / Major của driver, vd 576.
    /// </param>
    /// <param name="productMinor">
    /// Driver minor, always two digits (0..99); out-of-range values map to null.
    /// Minor của driver, luôn 2 chữ số (0..99); ngoài khoảng → null.
    /// </param>
    /// <returns>
    /// "cu129" when the driver reaches the 576.02 floor, or null otherwise (CPU flavor).
    /// "cu129" khi driver đạt sàn 576.02, hoặc null nếu không (flavor CPU).
    /// </returns>
    public static string? CudaWheelForDriverVersion(int productMajor, int productMinor)
    {
        if (productMinor is < 0 or > 99)
        {
            return null;
        }

        int version = productMajor * 100 + productMinor;
        return version >= MinDriverForCu129 ? "cu129" : null;
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
}
