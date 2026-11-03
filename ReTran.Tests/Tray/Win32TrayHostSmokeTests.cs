using System.Runtime.InteropServices;
using ReTran_App.Services.Tray;

public class Win32TrayHostSmokeTests
{
    [Fact]
    public void CreateRemoveDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var icon = new TrayIconSource();
        using var host = new Win32TrayHost();
        Assert.False(host.IsCreated);
        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false));

        // Window creation NEVER needs a shell — assert always, so the WM_NCCREATE/DefWindowProc
        // regression class is caught even on CI without an explorer/tray.
        // Tạo cửa sổ KHÔNG bao giờ cần shell — assert vô điều kiện, bắt đúng lớp regression
        // WM_NCCREATE/DefWindowProc kể cả CI không có explorer/tray.
        Assert.True(host.WindowCreated);

        host.Create(icon.Handle, "ReTran", () => TrayMenuModel.Build("a", "b", "c", false)); // idempotent

        // NIM_ADD needs a running shell (notification area). With explorer present the FIRST Create
        // must REALLY register (window + NIM_ADD) — not an unconditionally set flag. Without a
        // notification area (e.g. a CI runner with no explorer) IsCreated legitimately stays
        // false — honest failure reporting, not a defect to assert away. NOTE: Shell_TrayWnd is the
        // taskbar's CLASS name — FindWindowW takes (class, title), so it goes in the first slot.
        // NIM_ADD cần shell đang chạy (khu vực thông báo). Có explorer thì Create ĐẦU phải đăng ký
        // THẬT (window + NIM_ADD) — không phải cờ gán vô điều kiện. Không có khu vực thông báo
        // (vd. CI runner không explorer) thì IsCreated đúng lý là vẫn false — báo cáo thất bại
        // trung thực, không phải defect để gán bỏ. CHÚ Ý: Shell_TrayWnd là TÊN CLASS của taskbar —
        // FindWindowW nhận (class, title), nên nó nằm ở tham số đầu.
        if (NativeMethods.FindWindowW("Shell_TrayWnd", null) != 0)
            Assert.True(host.IsCreated);

        host.Remove();
        host.ShowBalloon("t", "m", BalloonIcon.Info); // no-op sau Remove, không throw
    }

    // Test-local P/Invoke: does this session actually have a shell/notification area?
    // P/Invoke riêng của test: phiên này có shell/khu vực thông báo thật không?
    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint FindWindowW(string? lpClassName, string? lpWindowName);
    }
}
