using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.Tray;

/// <summary>
/// Real Shell_NotifyIcon host: a message-only window created on the calling (UI) thread receives
/// the tray callbacks, dispatched by the pure <see cref="TrayContract"/> table. All public members
/// are called directly from that thread — no queue, because only the UI thread uses the host.
/// Host Shell_NotifyIcon thật: cửa sổ message-only tạo trên thread gọi (UI) nhận callback tray,
/// dispatch theo bảng thuần <see cref="TrayContract"/>. Mọi thành viên public được gọi trực tiếp từ
/// thread đó — không queue, vì chỉ UI thread dùng host.
/// </summary>
/// <remarks>
/// One host per process is expected: the window class WndProc is static and routes to the live
/// instance. Một process một host: WndProc của window class là static và chuyển tiếp đến instance
/// đang sống.
/// </remarks>
public sealed class Win32TrayHost : ITrayHost
{
    private const string WindowClassName = "ReTranTray";

    // szTip[128] / szInfoTitle[64] / szInfo[256] are fixed-width WCHAR buffers: keep room for NUL.
    // szTip[128] / szInfoTitle[64] / szInfo[256] là buffer WCHAR cố định: chừa chỗ cho NUL.
    private const int TipMax = 127;
    private const int BalloonTitleMax = 63;
    private const int BalloonTextMax = 255;

    private nint _hwnd;
    private bool _created;
    private bool _disposed;
    private nint _iconHandle;
    private string _tooltip = string.Empty;
    private Func<IReadOnlyList<TrayCommand>>? _menuProvider;
    private uint _taskbarCreatedId;
    private readonly ILogger<Win32TrayHost>? _log;

    // Roots the delegate so the native callback cannot be collected while the window lives.
    // Giữ delegate sống để callback native không bị thu hồi khi cửa sổ còn tồn tại.
    private static readonly NativeMethods.WNDPROC WndProcDelegate = StaticWndProc;

    // The class WndProc is shared and static; forward to the live host instance (one per process).
    // WndProc của class dùng chung và static; chuyển tiếp đến instance host đang sống (một mỗi process).
    private static Win32TrayHost? _instance;

    /// <summary>
    /// Creates the host; call <see cref="Create"/> from the UI thread to register the icon.
    /// Tạo host; gọi <see cref="Create"/> từ UI thread để đăng ký icon.
    /// </summary>
    /// <param name="log">
    /// Optional logger for best-effort failures (currently a refused NIM_SETVERSION); <see langword="null"/> keeps the host silent in tests.
    /// Logger tùy chọn cho các lỗi best-effort (hiện tại là NIM_SETVERSION bị từ chối); <see langword="null"/> để host im lặng trong test.
    /// </param>
    public Win32TrayHost(ILogger<Win32TrayHost>? log = null)
    {
        _log = log;
        _instance = this;
    }

    /// <inheritdoc/>
    public bool IsCreated => _created;

    /// <summary>
    /// Whether the message-only window exists, independently of whether the shell accepted the
    /// icon — pins window creation for tests running without a notification area (no shell on CI).
    /// Cửa sổ message-only có tồn tại hay không, độc lập với việc shell nhận icon — pin việc tạo
    /// cửa sổ cho test chạy mà không có khu vực thông báo (CI không có shell).
    /// </summary>
    internal bool WindowCreated => _hwnd != 0;

    /// <inheritdoc/>
    public void Create(nint iconHandle, string tooltip, Func<IReadOnlyList<TrayCommand>> menuProvider)
    {
        if (_disposed) return;
        _iconHandle = iconHandle;
        _tooltip = tooltip ?? string.Empty;
        _menuProvider = menuProvider;

        if (_created) return; // idempotent: window and icon already registered — idempotent: cửa sổ và icon đã đăng ký

        EnsureWindow();
        _taskbarCreatedId = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        bool iconAdded = AddIcon();
        // Report success only if the icon really is in the notification area: a failed message-only
        // window or a rejected NIM_ADD must leave IsCreated false so a failed registration cannot
        // masquerade as success. The stored icon/tooltip/menuProvider above and the registered window
        // class stay — the caller sees the failure and a later Create can retry with them.
        // Chỉ báo thành công khi icon thật sự nằm ở khu vực thông báo: tạo cửa sổ message-only hỏng
        // hoặc NIM_ADD bị từ chối phải để IsCreated bằng false, để một lần đăng ký hỏng không mạo
        // nhận thành công. Icon/tooltip/menuProvider đã gán ở trên và window class đã đăng ký giữ
        // nguyên — người gọi thấy thất bại và lần Create sau thử lại bằng chính chúng.
        _created = _hwnd != 0 && iconAdded;
    }

    /// <inheritdoc/>
    public void Remove()
    {
        // Symmetry with Create on the failure path: even when NIM_ADD was rejected (_created false),
        // the message-only window may exist and Dispose/Stop must reclaim the HWND — a failed
        // Create otherwise leaks one window per retry. NIM_DELETE is attempted only when the icon
        // is registered (deleting an unregistered icon would just return false anyway).
        // Đối xứng với Create trên đường thất bại: dù NIM_ADD bị từ chối (_created false), cửa sổ
        // message-only có thể còn đó và Dispose/Stop phải thu hồi HWND — nếu không mỗi lần Create
        // hỏng rò rỉ một cửa sổ. NIM_DELETE chỉ được thử khi icon đã đăng ký (xóa icon chưa đăng
        // ký vốn chỉ trả về false).
        if (_hwnd == 0) return;

        if (_created)
        {
            var nid = BaseNotifyIconData();
            NativeMethods.Shell_NotifyIconW(TrayContract.NimDelete, ref nid);
        }
        NativeMethods.DestroyWindow(_hwnd);
        _hwnd = 0;
        _created = false;
    }

    /// <inheritdoc/>
    public void ShowBalloon(string title, string text, BalloonIcon icon)
    {
        if (!_created) return; // no-op sau Remove — không throw

        var nid = BaseNotifyIconData();
        nid.uFlags = TrayContract.ModifyUFlags(withTip: true);
        nid.szInfoTitle = Truncate(title ?? string.Empty, BalloonTitleMax);
        nid.szInfo = Truncate(text ?? string.Empty, BalloonTextMax);
        nid.dwInfoFlags = TrayContract.InfoFlags(icon);
        NativeMethods.Shell_NotifyIconW(TrayContract.NimModify, ref nid);
    }

    /// <inheritdoc/>
    public event Action<TrayCommandId>? CommandSelected;

    /// <summary>
    /// Removes the icon and window if still registered, then releases the host (idempotent).
    /// Xóa icon và cửa sổ nếu còn đăng ký, rồi giải phóng host (idempotent).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }

    // Registers the window class once and creates the message-only window on the calling thread.
    // Đăng ký window class một lần và tạo cửa sổ message-only trên thread gọi.
    private void EnsureWindow()
    {
        if (_hwnd != 0) return;

        nint hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = WndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        NativeMethods.RegisterClassExW(ref wc); // already registered → 0, fine — đã đăng ký → 0, ổn

        _hwnd = NativeMethods.CreateWindowExW(
            0, WindowClassName, string.Empty, 0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    // NIM_ADD with the contract's flags, then NIM_SETVERSION pins the v4 callback layout explicitly.
    // Returns whether the shell accepted the icon (false when there is no window or Shell_NotifyIconW
    // refused the NIM_ADD); a refused NIM_SETVERSION is logged and does not undo the registration —
    // the menu position no longer depends on the payload version, so the icon stays fully usable.
    // NIM_ADD với cờ của contract, rồi NIM_SETVERSION ghim bố cục callback v4 một cách tường minh.
    // Trả về shell có nhận icon hay không (false khi không có cửa sổ hoặc Shell_NotifyIconW từ chối
    // NIM_ADD); NIM_SETVERSION bị từ chối thì chỉ ghi log và không hoàn tác đăng ký — vị trí menu
    // không còn phụ thuộc phiên bản payload nên icon vẫn dùng được trọn vẹn.
    private bool AddIcon()
    {
        if (_hwnd == 0) return false;

        var nid = BaseNotifyIconData();
        nid.uFlags = TrayContract.AddUFlags();
        nid.hIcon = _iconHandle;
        nid.szTip = Truncate(_tooltip, TipMax);
        nid.uVersion = TrayContract.NotifyIconVersion4;
        if (!NativeMethods.Shell_NotifyIconW(TrayContract.NimAdd, ref nid)) return false;

        // MSDN requires NIM_SETVERSION to switch the callback payload to v4; uVersion inside NIM_ADD
        // alone is not a guaranteed contract. Re-applied on every successful add, including the
        // re-add after an explorer restart. cbSize/hWnd/uID (set by BaseNotifyIconData) identify the
        // icon for the version switch.
        // MSDN yêu cầu NIM_SETVERSION để chuyển payload callback sang v4; chỉ đặt uVersion trong
        // NIM_ADD không phải hợp đồng được bảo đảm. Áp dụng lại ở mọi lần add thành công, kể cả
        // re-add sau khi explorer khởi động lại. cbSize/hWnd/uID (do BaseNotifyIconData đặt) định
        // danh icon cho lần chuyển phiên bản.
        if (!NativeMethods.Shell_NotifyIconW(TrayContract.NimSetVersion, ref nid))
        {
            _log?.LogWarning("NIM_SETVERSION was refused; the tray callback keeps the default payload.");
        }
        return true;
    }

    // Common NOTIFYICONDATA identifying this icon (hWnd + uID).
    // NOTIFYICONDATA chung định danh icon này (hWnd + uID).
    private NativeMethods.NOTIFYICONDATA BaseNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 0,
        uCallbackMessage = NativeMethods.CallbackMessage,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        _instance is { } host ? host.HandleMessage(hWnd, msg, wParam, lParam) : 0;

    // v4 shell (pinned explicitly by NIM_SETVERSION in AddIcon): LOWORD(lParam) carries the mouse
    // code. The menu opens at GetCursorPos, never at wParam — under a v0 payload wParam is the icon
    // id, not a coordinate pair.
    // Shell v4 (được ghim tường minh bằng NIM_SETVERSION trong AddIcon): từ thấp của lParam mang mã
    // chuột. Menu mở tại GetCursorPos, không bao giờ tại wParam — dưới payload v0, wParam là icon id,
    // không phải cặp tọa độ.
    // hWnd là THAM SỐ cửa sổ thật, không phải field _hwnd: WM_NCCREATE (và mọi message gửi trong
    // lúc CreateWindowExW còn chạy) đến TRƯỚC khi _hwnd được gán — DefWindowProc với _hwnd==0 trả
    // FALSE, việc tạo bị abort với ERROR_INVALID_WINDOW_HANDLE, và host không bao giờ có cửa sổ.
    // Với các message sau khi CreateWindowExW đã trả về, tham số và field là cùng một handle.
    // hWnd is the real WINDOW parameter, not the _hwnd field: WM_NCCREATE (and every message sent
    // while CreateWindowExW is still running) arrives BEFORE _hwnd is assigned — DefWindowProc on
    // _hwnd==0 returns FALSE, the create aborts with ERROR_INVALID_WINDOW_HANDLE, and the host
    // would never get a window at all. For messages after CreateWindowExW returned, parameter and
    // field are the same handle.
    private nint HandleMessage(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        uint mouseMsg = (uint)(lParam & 0xFFFF);
        switch (TrayContract.Dispatch(msg, _taskbarCreatedId, NativeMethods.CallbackMessage, mouseMsg))
        {
            case TrayContract.TrayDispatch.ReAddIcon:
                // Re-assign the honest flag: an explorer restart can place the icon even after a
                // rejected first NIM_ADD — with _created left false, the service (which only stops
                // what it believes it started) could never take that real icon down.
                // Gán lại cờ trung thực: explorer khởi động lại có thể đặt icon ngay cả sau NIM_ADD
                // đầu bị từ chối — nếu _created vẫn false thì service (chỉ dừng những gì nó cho là
                // mình đã start) sẽ không bao giờ gỡ được icon thật đó.
                _created = AddIcon(); // explorer restarted — re-register the icon — explorer khởi động lại — đăng ký lại icon
                return 0;

            case TrayContract.TrayDispatch.LeftClick:
                CommandSelected?.Invoke(TrayCommandId.OpenApp);
                return 0;

            case TrayContract.TrayDispatch.ShowMenu:
                // The cursor is always on the icon at right-click time — version-independent, so it
                // is correct under both the v4 and the v0 payload. Con trỏ luôn nằm trên icon lúc
                // right-click — độc lập phiên bản, nên đúng cho cả payload v4 lẫn v0.
                if (NativeMethods.GetCursorPos(out NativeMethods.POINT point))
                {
                    ShowContextMenu(point.x, point.y);
                }
                return 0;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // Builds the menu from the provider, tracks it at the given point and raises CommandSelected.
    // Dựng menu từ provider, hiện tại điểm cho trước và phát CommandSelected.
    private void ShowContextMenu(int x, int y)
    {
        if (_menuProvider is not { } provider) return;
        IReadOnlyList<TrayCommand> commands = provider();
        if (commands.Count == 0) return;

        nint hmenu = NativeMethods.CreatePopupMenu();
        if (hmenu == 0) return;

        for (int i = 0; i < commands.Count; i++)
        {
            TrayCommand command = commands[i];
            uint flags = NativeMethods.MF_STRING | (command.IsChecked ? NativeMethods.MF_CHECKED : 0u);
            NativeMethods.AppendMenuW(hmenu, flags, (uint)(i + 1), command.Label);
        }

        // The menu owner is _hwnd, NOT NULL: the shell needs a window of the calling thread to
        // create and track the popup, and a NULL owner is the classic way the menu silently never
        // appears on modern Windows. Ownership does not require a top-level window — a message-only
        // window qualifies (it still receives the menu messages on this thread).
        // Owner của menu là _hwnd, KHÔNG phải NULL: shell cần một cửa sổ thuộc thread đang gọi để
        // tạo và theo dõi popup, và owner NULL là cách kinh điển khiến menu im lặng không bao giờ
        // hiện trên Windows hiện đại. Làm owner không đòi hỏi cửa sổ top-level — cửa sổ message-only
        // thỏa (nó vẫn nhận tin nhắn menu trên chính thread này).
        // Best-effort foreground activation of our own window (the classic tray recipe) so a click
        // outside the menu dismisses it; a message-only window may never be allowed to take
        // foreground — the PostMessage(WM_NULL) after TrackPopupMenuEx stays and is what keeps the
        // dismissing click from being swallowed.
        // Kích hoạt foreground cho chính cửa sổ của mình (công thức tray cổ điển, best-effort) để cú
        // click bên ngoài đóng được menu; cửa sổ message-only có thể không bao giờ được nhận
        // foreground — cú PostMessage(WM_NULL) sau TrackPopupMenuEx vẫn giữ nguyên và là thứ khiến cú
        // click đóng menu không bị nuốt.
        NativeMethods.SetForegroundWindow(_hwnd);

        int cmd = NativeMethods.TrackPopupMenuEx(hmenu,
            NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD, x, y, _hwnd, IntPtr.Zero);

        // Clear the menu mode so the dismissing click is not swallowed.
        // Xóa chế độ menu để cú click đóng menu không bị nuốt.
        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        NativeMethods.DestroyMenu(hmenu);

        if (cmd > 0 && cmd <= commands.Count)
        {
            CommandSelected?.Invoke(commands[cmd - 1].Id);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    /// <summary>
    /// Win32 entry points used by the host (internal only).
    /// Các hàm Win32 mà host dùng (chỉ nội bộ).
    /// </summary>
    /// <remarks>
    /// Classic DllImport is used (not LibraryImport) so no project change
    /// (AllowUnsafeBlocks) is needed — same convention as Win32OverlayHost.NativeMethods.
    /// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
    /// (AllowUnsafeBlocks) — cùng quy ước với Win32OverlayHost.NativeMethods.
    /// </remarks>
    private static class NativeMethods
    {
        internal const uint WM_NULL = 0x0000;
        // WM_USER + 1: the shell delivers exactly this message id (it does not add WM_USER).
        // This value (0x0401) coincidentally equals TrayContract.NinKeySelect, but that constant is
        // a mouse code read from LOWORD(lParam) — a different field — so both must stay.
        // WM_USER + 1: shell gửi đúng mã tin nhắn này (không cộng thêm WM_USER).
        // Giá trị này (0x0401) tình cờ trùng TrayContract.NinKeySelect, nhưng hằng đó là mã chuột
        // đọc từ LOWORD(lParam) — field khác — nên cả hai phải được giữ nguyên.
        internal const uint CallbackMessage = 0x0401;

        internal const uint MF_STRING = 0x0000;
        internal const uint MF_CHECKED = 0x0008;
        internal const uint TPM_RIGHTBUTTON = 0x0002;
        internal const uint TPM_RETURNCMD = 0x0100;

        internal static readonly nint HWND_MESSAGE = new(-3);

        internal delegate nint WNDPROC(nint hWnd, uint msg, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            internal uint cbSize;
            internal uint style;
            internal WNDPROC lpfnWndProc;
            internal int cbClsExtra;
            internal int cbWndExtra;
            internal nint hInstance;
            internal nint hIcon;
            internal nint hCursor;
            internal nint hbrBackground;
            internal string? lpszMenuName;
            internal string lpszClassName;
            internal nint hIconSm;
        }

        // v4 layout (szInfo[256] / szInfoTitle[64] / dwInfoFlags); cbSize comes from Marshal.SizeOf.
        // Bố cục v4 (szInfo[256] / szInfoTitle[64] / dwInfoFlags); cbSize lấy từ Marshal.SizeOf.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NOTIFYICONDATA
        {
            internal uint cbSize;
            internal nint hWnd;
            internal uint uID;
            internal uint uFlags;
            internal uint uCallbackMessage;
            internal nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string szTip;
            internal uint dwState;
            internal uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            internal string szInfo;
            internal uint uVersion; // union with uTimeout — gộp với uTimeout
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            internal string szInfoTitle;
            internal uint dwInfoFlags;
        }

        // POINT: screen coordinates, origin at the top-left of the primary monitor.
        // POINT: tọa độ màn hình, gốc tại góc trên-trái của màn hình chính.
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int x;
            internal int y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessageW(string messageName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint hWnd);

        // Screen coordinates of the cursor; the tray menu opens here (version-independent).
        // Tọa độ màn hình của con trỏ; menu tray mở tại đây (độc lập phiên bản).
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        internal static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

        // Real 6-parameter signature (hmenu, fuFlags, x, y, hwnd, lptpm). The previous declaration
        // had a bogus 7th-slot "anchorId" parameter that shifted the trailing arguments: the native
        // side read C# arg 5 as hwnd (0 = NULL owner) and C# arg 6 as lptpm — the tray menu could
        // never really own a window. Returns the selected command id (TPM_RETURNCMD), not a BOOL.
        // Đúng chữ ký 6 tham số (hmenu, fuFlags, x, y, hwnd, lptpm). Khai báo cũ có thêm tham số thừa
        // "anchorId" làm lệch các tham số cuối: phía native đọc tham số 5 của C# thành hwnd (0 =
        // owner NULL) và tham số 6 thành lptpm — menu tray không bao giờ thật sự có cửa sổ owner.
        // Trả về mã lệnh được chọn (TPM_RETURNCMD), không phải BOOL.
        [DllImport("user32.dll")]
        internal static extern int TrackPopupMenuEx(
            nint hMenu, uint uFlags, int x, int y, nint hwnd, nint tpParams);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint hMenu);
    }
}
