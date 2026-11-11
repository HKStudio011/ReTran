using System.Runtime.InteropServices;
using ReTran_App.Services.FloatingMenu;
using ReTran_App.Services.OverlayWindow;

public class Win32MenuHostTests
{
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;       // the click lands on the menu
    private const int HTTRANSPARENT = -1; // the click falls through to the window below

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out RECT rect);

    /// <summary>Packs a screen point exactly the way Windows packs mouse-message lParams: two signed 16-bit words, x low / y high. Đóng gói điểm màn hình đúng cách Windows đóng gói lParam của message chuột: hai từ 16-bit có dấu, x thấp / y cao.</summary>
    private static nint MakeLParam(int x, int y) => new((y << 16) | (x & 0xFFFF));

    /// <summary>Waits until the menu window's rect matches the ShowMenu target — the barrier proving the ShowMenu action (and its stored size) has run before probes are sent. Chờ tới khi rect của cửa sổ menu khớp đích ShowMenu — mốc bảo đảm action ShowMenu (và kích thước đã lưu) đã chạy trước khi gửi probe.</summary>
    private static void WaitForWindowRect(nint hwnd, int x, int y, int w, int h)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (GetWindowRect(hwnd, out RECT r)
                && r.Left == x && r.Top == y && r.Right - r.Left == w && r.Bottom - r.Top == h)
                return;
            Thread.Sleep(10);
        }
        Assert.Fail($"menu window rect never became ({x},{y},{w}x{h}) — ShowMenu's action did not run");
    }

    // Review Focus #2: the box window must insert after the menu hwnd so it always sits below
    // ReTran's own menu (both are topmost); fall back to HWND_TOPMOST before the menu exists.
    [Fact]
    public void BoxInsertAfter_MenusHwndWhenPresent()
    {
        Assert.Equal(42L, Win32OverlayHost.BoxInsertAfter(42).ToInt64());
        // HWND_TOPMOST is (nint)-1.
        Assert.Equal(-1L, Win32OverlayHost.BoxInsertAfter(0).ToInt64());
    }

    // Review Focus #1: the menu window must be topmost and non-activating, but NOT click-through
    // — it has to receive clicks (WS_EX_TRANSPARENT would eat them).
    [Fact]
    public void MenuWindowStyles_NoActivateAndTopmost()
    {
        uint styles = Win32OverlayHost.MenuWindowExStyles();

        const uint WS_EX_TOPMOST = 0x0008_0000;
        const uint WS_EX_NOACTIVATE = 0x0800_0000;
        const uint WS_EX_TRANSPARENT = 0x0000_0020;

        Assert.True((styles & WS_EX_TOPMOST) != 0, "expected WS_EX_TOPMOST to be set");
        Assert.True((styles & WS_EX_NOACTIVATE) != 0, "expected WS_EX_NOACTIVATE to be set");
        Assert.True((styles & WS_EX_TRANSPARENT) == 0, "WS_EX_TRANSPARENT must NOT be set — the menu receives clicks");
    }

    // Real-Win32 smoke: create → show → draw → hide → dispose without throwing.
    [Fact]
    public void CreateShowDrawHideDispose_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new Win32OverlayHost();
        host.CreateMenu();
        Assert.True(host.MenuCreated); // IMenuWindowHost.MenuCreated
        host.ShowMenu(100, 100, 344, 500);
        using var bmp = new System.Drawing.Bitmap(344, 500, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        host.DrawMenu(bmp);
        host.HideMenu();
        host.Dispose();
    }

    // T23e real-Win32 regression (production bug 2026-11-12): WM_NCHITTEST must decode lParam the
    // way Windows packs MOUSE messages — two SIGNED 16-BIT words (LOWORD = x, HIWORD = y) — not as
    // two 32-bit dwords. With the wrong decode every real point became garbage (y = 0, x = the
    // whole packed value), every point fell outside the panel and EVERY click fell through the
    // menu to the window below (live: a synthetic click hit the app switch under the menu).
    // T23e regression real-Win32 (bug production 2026-11-12): WM_NCHITTEST phải giải mã lParam
    // đúng cách Windows đóng gói message CHUỘT — hai từ 16-BIT có dấu (LOWORD = x, HIWORD = y) —
    // không phải hai dword 32-bit. Giải mã sai làm mọi điểm thật thành rác (y = 0, x = cả giá trị
    // đóng gói), mọi điểm rơi ngoài panel và MỌI click rơi xuyên menu xuống cửa sổ dưới.
    [Fact]
    public void NcHitTest_UsesMouseMessagePacking_PanelIsClientShadowRingFallsThrough()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new Win32OverlayHost();
        host.CreateMenu();
        Assert.True(host.MenuCreated);
        nint hwnd = host.MenuHwnd;
        Assert.NotEqual(0, hwnd);

        host.ShowMenu(100, 100, 344, 500);
        WaitForWindowRect(hwnd, 100, 100, 344, 500); // the stored size is live before probing

        // Interior point: window (118,118) = (100 + ShadowMargin + 10) — inside the rounded panel.
        // Điểm trong panel: window (118,118) — nằm trong panel bo tròn.
        long inside = SendMessageW(hwnd, WM_NCHITTEST, 0, MakeLParam(100 + FloatingMenuLayout.ShadowMargin + 10,
            100 + FloatingMenuLayout.ShadowMargin + 10)).ToInt64();
        Assert.Equal(HTCLIENT, inside);

        // Shadow-ring point (100+2,100+2) — outside the panel inset, the click must fall through.
        // Điểm vành shadow (100+2,100+2) — ngoài phần lõm của panel, click phải rơi xuyên qua.
        long ring = SendMessageW(hwnd, WM_NCHITTEST, 0, MakeLParam(100 + 2, 100 + 2)).ToInt64();
        Assert.Equal(HTTRANSPARENT, ring);
    }

    // T23e: the same packing drives the press/release state machine — a synthetic DOWN/UP pair at
    // an interior point must come back as a real PanelClicked event with PANEL coordinates
    // (origin = panel top-left, i.e. window coords minus the shadow margin).
    // Cùng cách đóng gói chi phối máy trạng thái nhấn/nhả — cặp DOWN/UP tổng hợp tại điểm trong
    // panel phải trả về event PanelClicked thật với toạ độ PANEL (gốc = góc trên trái panel).
    [Fact]
    public void MouseDownUpInsidePanel_RaisesPanelClickedInPanelCoordinates()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new Win32OverlayHost();
        host.CreateMenu();
        Assert.True(host.MenuCreated);
        nint hwnd = host.MenuHwnd;

        host.ShowMenu(100, 100, 344, 500);
        WaitForWindowRect(hwnd, 100, 100, 344, 500);

        int clickedX = -1, clickedY = -1;
        using var clicked = new ManualResetEventSlim(false);
        host.PanelClicked += (x, y) => { clickedX = x; clickedY = y; clicked.Set(); };

        int m = FloatingMenuLayout.ShadowMargin;
        nint point = MakeLParam(100 + m + 10, 100 + m + 10);
        SendMessageW(hwnd, WM_LBUTTONDOWN, 0, point);
        SendMessageW(hwnd, WM_LBUTTONUP, 0, point);

        Assert.True(clicked.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken),
            "WM_LBUTTONUP inside the panel must raise PanelClicked");
        Assert.Equal((10, 10), (clickedX, clickedY)); // 118 − 100 − 8 in both axes
    }
}
