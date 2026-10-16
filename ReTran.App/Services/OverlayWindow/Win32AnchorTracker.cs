using System.Globalization;
using System.Runtime.InteropServices;
using ReTran_App.Core;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Resolves the on-screen rect of the active capture source with Win32 calls: window bounds
/// for window sources, monitor bounds (by enumeration order) for screen sources.
/// Tìm hình chữ nhật màn hình của nguồn capture đang hoạt động bằng các gọi Win32: khổ cửa sổ
/// cho nguồn cửa sổ, khổ monitor (theo thứ tự liệt kê) cho nguồn màn hình.
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

        if (source.UseWindow) return GetWindowRect(source.HwndHex);
        return GetMonitorRect(source.Monitor);
    }

    private static (int X, int Y, int Width, int Height)? GetWindowRect(string? hwndHex)
    {
        if (!TryParseHwnd(hwndHex, out nint hwnd)) return null;
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        return width <= 0 || height <= 0 ? null : (rect.Left, rect.Top, width, height);
    }

    private static (int X, int Y, int Width, int Height)? GetMonitorRect(int index)
    {
        var monitors = new List<(int X, int Y, int Width, int Height)>();
        NativeMethods.MonitorEnumProc callback = (nint hMonitor, nint hdc, ref NativeMethods.RECT rect, nint data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                monitors.Add((
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top));
            }
            return true;
        };

        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return index >= 0 && index < monitors.Count ? monitors[index] : null;
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

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
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
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO info);

        internal static bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info) => GetMonitorInfoW(hMonitor, ref info);
    }
}
