using System.Runtime.InteropServices;

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

    // Roots the delegate so the native callback cannot be collected while the window lives.
    // Giữ delegate sống để callback native không bị thu hồi khi cửa sổ còn tồn tại.
    private static readonly NativeMethods.WNDPROC WndProcDelegate = StaticWndProc;

    // The class WndProc is shared and static; forward to the live host instance (one per process).
    // WndProc của class dùng chung và static; chuyển tiếp đến instance host đang sống (một mỗi process).
    private static Win32TrayHost? _instance;

    /// <summary>Creates the host; call <see cref="Create"/> from the UI thread to register the icon. Tạo host; gọi <see cref="Create"/> từ UI thread để đăng ký icon.</summary>
    public Win32TrayHost() => _instance = this;

    /// <inheritdoc/>
    public bool IsCreated => _created;

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
        AddIcon();
        _created = true;
    }

    /// <inheritdoc/>
    public void Remove()
    {
        if (!_created) return;

        var nid = BaseNotifyIconData();
        NativeMethods.Shell_NotifyIconW(TrayContract.NimDelete, ref nid);
        if (_hwnd != 0)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
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

    // NIM_ADD with the contract's flags and uVersion = 4 (v4 callback layout).
    // NIM_ADD với cờ của contract và uVersion = 4 (bố cục callback v4).
    private void AddIcon()
    {
        if (_hwnd == 0) return;

        var nid = BaseNotifyIconData();
        nid.uFlags = TrayContract.AddUFlags();
        nid.hIcon = _iconHandle;
        nid.szTip = Truncate(_tooltip, TipMax);
        nid.uVersion = 4;
        NativeMethods.Shell_NotifyIconW(TrayContract.NimAdd, ref nid);
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
        _instance is { } host ? host.HandleMessage(msg, wParam, lParam) : 0;

    // v4 shell: LOWORD(lParam) carries the mouse code, screen coordinates live in wParam.
    // Shell v4: từ thấp của lParam mang mã chuột, tọa độ màn hình nằm trong wParam.
    private nint HandleMessage(uint msg, nint wParam, nint lParam)
    {
        uint mouseMsg = (uint)(lParam & 0xFFFF);
        switch (TrayContract.Dispatch(msg, _taskbarCreatedId, NativeMethods.CallbackMessage, mouseMsg))
        {
            case TrayContract.TrayDispatch.ReAddIcon:
                AddIcon(); // explorer restarted — re-register the icon — explorer khởi động lại — đăng ký lại icon
                return 0;

            case TrayContract.TrayDispatch.LeftClick:
                CommandSelected?.Invoke(TrayCommandId.OpenApp);
                return 0;

            case TrayContract.TrayDispatch.ShowMenu:
                ShowContextMenu(GetX(wParam), GetY(wParam));
                return 0;
        }
        return NativeMethods.DefWindowProc(_hwnd, msg, wParam, lParam);
    }

    // GET_X_LPARAM / GET_Y_LPARAM equivalents (sign-extended: multi-monitor coords can be negative).
    // Tương đương GET_X_LPARAM / GET_Y_LPARAM (kéo dài dấu: tọa độ đa màn hình có thể âm).
    private static int GetX(nint value) => unchecked((short)(value & 0xFFFF));
    private static int GetY(nint value) => unchecked((short)(((nuint)value >> 16) & 0xFFFF));

    // Builds the menu from the provider, tracks it at the click point and raises CommandSelected.
    // Dựng menu từ provider, hiện tại điểm click và phát CommandSelected.
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

        // Reset the foreground state so TrackPopupMenu's click-to-dismiss works.
        // Đặt lại trạng thái foreground để hành vi click-để-đóng của TrackPopupMenu hoạt động.
        NativeMethods.SetForegroundWindow(NativeMethods.GetForegroundWindow());

        int cmd = NativeMethods.TrackPopupMenuEx(hmenu,
            NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD, x, y, 0, IntPtr.Zero, IntPtr.Zero);

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
        // WM_USER + 1: shell gửi đúng mã tin nhắn này (không cộng thêm WM_USER).
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

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

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

        // Returns the selected command id (TPM_RETURNCMD), not a BOOL.
        // Trả về mã lệnh được chọn (TPM_RETURNCMD), không phải BOOL.
        [DllImport("user32.dll")]
        internal static extern int TrackPopupMenuEx(
            nint hMenu, uint uFlags, int x, int y, uint anchorId, nint hwnd, nint tpParams);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint hMenu);
    }
}
