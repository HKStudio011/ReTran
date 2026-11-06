using System.Globalization;
using System.Runtime.InteropServices;
using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Resolves the on-screen rect of the active capture source with Win32 calls: the client area
/// (mapped to screen coordinates) for window sources, monitor bounds (by enumeration order) for
/// screen sources. The client area matches what <c>WindowCaptureSource</c> captures; using the
/// full window rect would offset every box by the title bar and borders.
/// Tìm hình chữ nhật màn hình của nguồn capture đang hoạt động bằng các gọi Win32: vùng client
/// (đổi sang toạ độ màn hình) cho nguồn cửa sổ, khổ monitor (theo thứ tự liệt kê) cho nguồn màn
/// hình. Vùng client khớp với thứ <c>WindowCaptureSource</c> chụp; dùng khổ cửa sổ đầy đủ sẽ
/// làm mọi box lệch đúng bằng title bar và viền.
/// </summary>
public sealed class Win32AnchorTracker : IAnchorTracker
{
    /// <summary>
    /// Returns the source's rectangle in physical pixels, or <see langword="null"/> when the
    /// source is gone, minimized, unparseable or off-Windows.
    /// Trả về hình chữ nhật của nguồn theo pixel vật lý, hoặc <see langword="null"/> khi nguồn
    /// biến mất, bị thu nhỏ, không parse được hoặc chạy ngoài Windows.
    /// </summary>
    /// <param name="source">The capture source to locate. Nguồn capture cần xác định vị trí.</param>
    public (int X, int Y, int Width, int Height)? GetAnchorRect(SourceSelection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!OperatingSystem.IsWindows()) return null;

        if (source.UseWindow) return GetClientRect(source.HwndHex);
        return GetMonitorRect(source.Monitor);
    }

    /// <inheritdoc/>
    public bool IsSourceForeground(SourceSelection source, nint? foregroundWindow = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!OperatingSystem.IsWindows()) return false;

        nint foreground = foregroundWindow is { } reported && reported != 0
            ? reported
            : NativeMethods.GetForegroundWindow();

        // A monitor overlay keeps translating the screen while other apps are focused; only the
        // controller itself, focused on the same monitor, hides it.
        // Overlay toàn màn hình vẫn dịch màn hình khi app khác focus; chỉ khi chính cửa sổ điều
        // khiển được focus trên cùng monitor thì mới ẩn.
        if (!source.UseWindow) return IsMonitorOverlayVisible(source.Monitor, foreground);

        if (foreground == 0) return false;
        if (!TryParseHwnd(source.HwndHex, out nint hwnd) || !NativeMethods.IsWindow(hwnd)) return false;
        if (foreground == hwnd) return true;

        // Same app: a dialog or secondary top-level window of the source keeps the overlay up.
        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint sourcePid);
        if (foregroundPid != 0 && foregroundPid == sourcePid) return true;

        // Another app focused on another monitor does not cover the overlay: keep it visible.
        nint sourceMonitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        nint foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return sourceMonitor != 0 && foregroundMonitor != 0 && sourceMonitor != foregroundMonitor;
    }

    private static bool IsMonitorOverlayVisible(int monitorIndex, nint foreground)
    {
        if (foreground == 0) return true;

        NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        if (foregroundPid != (uint)Environment.ProcessId) return true;

        // The ReTran controller is focused: hide unless it sits on another monitor than the overlay.
        // Cửa sổ ReTran đang focus: ẩn trừ khi nó nằm ở monitor khác với overlay.
        nint overlayMonitor = GetMonitorHandle(monitorIndex);
        nint foregroundMonitor = NativeMethods.MonitorFromWindow(foreground, NativeMethods.MONITOR_DEFAULTTONEAREST);
        return overlayMonitor == 0 || foregroundMonitor == 0 || overlayMonitor != foregroundMonitor;
    }

    private static (int X, int Y, int Width, int Height)? GetClientRect(string? hwndHex)
    {
        if (!TryParseHwnd(hwndHex, out nint hwnd)) return null;
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;
        if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var origin = new NativeMethods.POINT { X = 0, Y = 0 };
        if (!NativeMethods.ClientToScreen(hwnd, ref origin)) return null;

        return (origin.X, origin.Y, width, height);
    }

    private static (int X, int Y, int Width, int Height)? GetMonitorRect(int index)
    {
        List<(nint Handle, int X, int Y, int Width, int Height)> monitors = EnumerateMonitors();
        return index >= 0 && index < monitors.Count
            ? (monitors[index].X, monitors[index].Y, monitors[index].Width, monitors[index].Height)
            : null;
    }

    private static nint GetMonitorHandle(int index)
    {
        List<(nint Handle, int X, int Y, int Width, int Height)> monitors = EnumerateMonitors();
        return index >= 0 && index < monitors.Count ? monitors[index].Handle : 0;
    }

    private static List<(nint Handle, int X, int Y, int Width, int Height)> EnumerateMonitors()
    {
        var monitors = new List<(nint Handle, int X, int Y, int Width, int Height)>();
        NativeMethods.MonitorEnumProc callback = (nint hMonitor, nint hdc, ref NativeMethods.RECT rect, nint data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add((
                    hMonitor,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top));
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return monitors;
    }

    private static bool TryParseHwnd(string? hex, out nint hwnd)
    {
        hwnd = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        string s = hex.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value) || value == 0)
        {
            return false;
        }

        hwnd = new nint(value);
        return true;
    }

    /// <summary>
    /// Win32 entry points used by the tracker (internal only).
    /// Các hàm Win32 mà tracker dùng (chỉ nội bộ).
    /// </summary>
    /// <remarks>
    /// Classic DllImport is used (not LibraryImport) so no project change
    /// (AllowUnsafeBlocks) is needed — same convention as ReTran.Core.Capture.NativeMethods.
    /// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
    /// (AllowUnsafeBlocks) — cùng quy ước với ReTran.Core.Capture.NativeMethods.
    /// </remarks>
    private static class NativeMethods
    {
        internal const int MONITORINFOF_PRIMARY = 1;
        internal const uint MONITOR_DEFAULTTONEAREST = 0x0000_0002;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO
        {
            internal int cbSize;
            internal RECT rcMonitor;
            internal RECT rcWork;
            internal int dwFlags;
        }

        internal delegate bool MonitorEnumProc(nint hMonitor, nint hdc, ref RECT rect, nint data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern nint MonitorFromWindow(nint hWnd, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ClientToScreen(nint hWnd, ref POINT point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

        internal static bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info) => GetMonitorInfoW(hMonitor, ref info);
    }
}
