using System.Management;

namespace ReTran_App.Services.Setup;

/// <summary>
/// <see cref="IVideoAdapterSource"/> backed by WMI (Win32_VideoController). Fully defensive: any WMI
/// failure (service stopped, COM error, access denied) yields an empty inventory so SystemCheck
/// degrades to the CPU flavor instead of crashing the wizard. DriverVersion is the raw WMI form
/// (for NVIDIA the D3D/INF version, e.g. "32.0.16.1742" on the dev machine) — mapping to NVIDIA's
/// product version lives in <see cref="SystemCheck.TryGetNvidiaProductVersion"/>.
/// <see cref="IVideoAdapterSource"/> dùng WMI (Win32_VideoController). Phòng thủ tuyệt đối: mọi lỗi WMI
/// (dịch vụ tắt, lỗi COM, từ chối truy cập) trả danh sách rỗng để SystemCheck hạ xuống flavor CPU thay
/// vì làm wizard crash. DriverVersion là dạng WMI thô (với NVIDIA là dạng D3D/INF, vd "32.0.16.1742"
/// trên máy dev) — việc ánh xạ sang product version của NVIDIA nằm ở
/// <see cref="SystemCheck.TryGetNvidiaProductVersion"/>.
/// </summary>
public sealed class VideoAdapterSource : IVideoAdapterSource
{
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
        catch (Exception)
        {
            // WMI có thể hỏng/không dùng được (WMIPrvSE stopped, access denied): degrade — SystemCheck
            // khi đó không thấy NVIDIA nào và chọn CPU kèm notice, wizard không crash.
            // WMI may be unavailable/broken: degrade; SystemCheck then sees no NVIDIA and picks CPU
            // with a notice instead of the wizard crashing.
            return Array.Empty<VideoAdapterInfo>();
        }

        return adapters;
    }

    /// <summary>
    /// Name-based discrete heuristic (plan Task 6): AMD counts as discrete only when the name carries
    /// both "Radeon" and a discrete product pattern ("RX ") — AMD APUs ("...Radeon Graphics") do not
    /// match; NVIDIA GeForce/Quadro/Tesla/NVS families count as discrete (their CUDA capability is
    /// decided by the driver ladder, not by this heuristic); anything else defaults to non-discrete.
    /// Heuristic card rời dựa trên tên (plan Task 6): AMD chỉ là rời khi tên chứa cả "Radeon" lẫn
    /// pattern sản phẩm rời ("RX ") — APU AMD ("...Radeon Graphics") không khớp; họ NVIDIA
    /// GeForce/Quadro/Tesla/NVS tính là rời (khả năng CUDA do bậc thang driver quyết, không do heuristic
    /// này); còn lại mặc định không rời.
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
