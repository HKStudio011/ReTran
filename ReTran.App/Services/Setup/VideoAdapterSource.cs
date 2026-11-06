using System.Management;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Setup;

/// <summary>
/// <see cref="IVideoAdapterSource"/> backed by WMI (Win32_VideoController). Fully defensive: any WMI
/// failure (service stopped, COM error, access denied) yields an empty inventory so SystemCheck
/// degrades to the CPU flavor instead of crashing the wizard — the failure is logged, because on a
/// healthy NVIDIA machine an empty inventory otherwise reads as "no supported NVIDIA GPU".
/// DriverVersion is the raw WMI form (for NVIDIA the INF form, e.g. "32.0.16.2114" on the dev machine,
/// whose branch segment 16 is what encodes the 6xx product family) — mapping to NVIDIA's product
/// version lives in <see cref="SystemCheck.TryGetNvidiaProductVersion"/>.
/// <see cref="IVideoAdapterSource"/> dùng WMI (Win32_VideoController). Phòng thủ tuyệt đối: mọi lỗi WMI
/// (dịch vụ tắt, lỗi COM, từ chối truy cập) trả danh sách rỗng để SystemCheck hạ xuống flavor CPU thay
/// vì làm wizard crash — lỗi được log, vì trên máy NVIDIA khỏe mà danh sách rỗng thì người dùng chỉ còn
/// thấy "không có GPU NVIDIA hỗ trợ". DriverVersion là dạng WMI thô (với NVIDIA là dạng INF, vd
/// "32.0.16.2114" trên máy dev, mà segment branch 16 mới là thứ mã hóa họ sản phẩm 6xx) — việc ánh xạ
/// sang product version của NVIDIA nằm ở <see cref="SystemCheck.TryGetNvidiaProductVersion"/>.
/// </summary>
public sealed class VideoAdapterSource : IVideoAdapterSource
{
    private readonly ILogger<VideoAdapterSource>? _logger;

    /// <summary>
    /// Creates the WMI-backed source. The logger is optional (and null-allowed) because this class is not
    /// constructed by DI yet — Task 14 registers it; until then callers can `new` it without a logger and
    /// the WMI failure path simply stays silent instead of throwing.
    /// Tạo nguồn backed by WMI. Logger là tham số optional (và cho phép null) vì class này chưa được DI
    /// khởi tạo — Task 14 mới đăng ký; trước đó caller có thể `new` mà không cần logger và nhánh lỗi WMI
    /// chỉ im lặng thay vì ném exception.
    /// </summary>
    /// <param name="logger">
    /// Logger for WMI failures, or null to keep the degrade path silent. / Logger cho lỗi WMI, hoặc null
    /// để nhánh degrade im lặng.
    /// </param>
    public VideoAdapterSource(ILogger<VideoAdapterSource>? logger = null) => _logger = logger;

    /// <inheritdoc />
    public IReadOnlyList<VideoAdapterInfo> GetAdapters()
    {
        var adapters = new List<VideoAdapterInfo>();
        try
        {
            var query = new ObjectQuery("SELECT Name, DriverVersion FROM Win32_VideoController");
            using var searcher = new ManagementObjectSearcher(query);
            using var collected = searcher.Get();
            foreach (var obj in collected)
            {
                using var managementObject = (ManagementObject)obj;
                if (managementObject["Name"] is not string name || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? driverVersion = managementObject["DriverVersion"] as string;
                adapters.Add(new VideoAdapterInfo(name.Trim(), IsDiscreteName(name), driverVersion));
            }
        }
        catch (Exception ex)
        {
            // WMI có thể hỏng/không dùng được (WMIPrvSE stopped, access denied): degrade — SystemCheck
            // khi đó không thấy NVIDIA nào và chọn CPU kèm notice, wizard không crash. Log lỗi: nếu
            // không, máy RTX khỏe chỉ thấy notice "không có GPU NVIDIA hỗ trợ" mà không có dấu vết gì.
            // WMI may be unavailable/broken (WMIPrvSE stopped, access denied): degrade; SystemCheck then
            // sees no NVIDIA and picks CPU with a notice instead of the wizard crashing. Log it: without a
            // trace, a healthy RTX machine only ever shows a "no supported NVIDIA GPU" notice.
            _logger?.LogWarning(ex,
                "WMI Win32_VideoController query failed; the adapter inventory is empty, so the setup " +
                "check will fall back to the CPU flavor even if this machine has a supported NVIDIA GPU.");
            return Array.Empty<VideoAdapterInfo>();
        }

        return adapters;
    }

    /// <summary>
    /// Name-based discrete heuristic (plan Task 6): AMD counts as discrete only when the name carries
    /// both "Radeon" and a discrete product pattern ("RX ") — AMD APUs ("...Radeon Graphics") do not
    /// match; NVIDIA GeForce/Quadro/Tesla/NVS families count as discrete (their CUDA capability is
    /// decided by the driver ladder, not by this heuristic); anything else defaults to non-discrete.
    /// Since <see cref="SystemCheck"/> now requires a DISCRETE NVIDIA adapter before picking the GPU
    /// flavor, this heuristic also gates NVIDIA: a pro/data-center name without one of those tokens
    /// ("NVIDIA T4", "NVIDIA RTX 6000 Ada Generation") or a virtual adapter ("NVIDIA Virtual Display
    /// Adapter") counts as non-discrete and falls back to CPU — the fail-safe direction.
    /// Heuristic card rời dựa trên tên (plan Task 6): AMD chỉ là rời khi tên chứa cả "Radeon" lẫn
    /// pattern sản phẩm rời ("RX ") — APU AMD ("...Radeon Graphics") không khớp; họ NVIDIA
    /// GeForce/Quadro/Tesla/NVS tính là rời (khả năng CUDA do bậc thang driver quyết, không do heuristic
    /// này); còn lại mặc định không rời. Vì <see cref="SystemCheck"/> bây giờ yêu cầu adapter NVIDIA
    /// PHẢI RỜI trước khi chọn flavor GPU, heuristic này cũng gate luôn NVIDIA: tên pro/data-center không
    /// mang token nào ("NVIDIA T4", "NVIDIA RTX 6000 Ada Generation") hoặc adapter ảo ("NVIDIA Virtual
    /// Display Adapter") bị coi là không rời và rớt xuống CPU — hướng fail-safe.
    /// </summary>
    internal static bool IsDiscreteName(string name) =>
        (NameContains(name, "Radeon") && NameContains(name, "RX "))
        || NameContains(name, "GeForce")
        || NameContains(name, "Quadro")
        || NameContains(name, "Tesla")
        || NameContains(name, "NVS");

    private static bool NameContains(string name, string token) =>
        name.Contains(token, StringComparison.OrdinalIgnoreCase);
}
