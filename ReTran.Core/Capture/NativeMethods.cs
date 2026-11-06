namespace ReTran.Core.Capture;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Win32 GDI/User32 entry points for per-window capture (internal only; not part of the public API).
/// Các hàm Win32 GDI/User32 phục vụ chụp từng cửa sổ (chỉ dùng nội bộ; không thuộc API công khai).
/// </summary>
/// <remarks>
/// Classic DllImport is used (not LibraryImport) so no project change
/// (AllowUnsafeBlocks) is needed.
/// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
/// (AllowUnsafeBlocks).
/// </remarks>
internal static class NativeMethods
{
    /// <summary>PrintWindow flag asking the window to render its full content (including covered areas). Cờ PrintWindow yêu cầu cửa sổ vẽ toàn bộ nội dung (kể cả vùng bị che).</summary>
    internal const uint PW_RENDERFULLCONTENT = 0x02;

    /// <summary>Uncompressed DIB format. Định dạng DIB không nén.</summary>
    internal const uint BI_RGB = 0;

    /// <summary>No color table mapping for GetDIBits/CreateDIBSection. Không dùng bảng màu khi gọi GetDIBits/CreateDIBSection.</summary>
    internal const uint DIB_RGB_COLORS = 0;

    /// <summary>BitBlt raster operation: copy source pixels as-is. Thao tác BitBlt: copy nguyên pixel nguồn.</summary>
    internal const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// Per-Monitor-V2 awareness value (DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4).
    /// Giá trị nhận thức DPI Per-Monitor-V2 (DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4).
    /// </summary>
    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    /// <summary>Client-area rectangle. Hình chữ nhật vùng client.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    /// <summary>DIB header for a 32-bit BI_RGB bitmap (no palette follows). Header DIB cho bitmap 32-bit BI_RGB (không có palette đi kèm).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        internal uint biSize;
        internal int biWidth;
        internal int biHeight;
        internal ushort biPlanes;
        internal ushort biBitCount;
        internal uint biCompression;
        internal uint biSizeImage;
        internal int biXPelsPerMeter;
        internal int biYPelsPerMeter;
        internal uint biClrUsed;
        internal uint biClrImportant;
    }

    /// <summary>Bitmap description passed to CreateDIBSection/GetDIBits (header only; 32-bit needs no color table). Mô tả bitmap truyền cho CreateDIBSection/GetDIBits (chỉ header; 32-bit không cần bảng màu).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        internal BITMAPINFOHEADER bmiHeader;
    }

    /// <summary>Callback invoked once per top-level window by EnumWindows. Hàm回调 được EnumWindows gọi một lần cho mỗi cửa sổ top-level.</summary>
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    /// <summary>Enumerates all top-level windows. Liệt kê mọi cửa sổ top-level.</summary>
    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    /// <summary>Reads a window's title into the buffer. Đọc tiêu đề cửa sổ vào buffer.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>Reports whether a window is visible. Cho biết cửa sổ có đang hiển thị hay không.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    /// <summary>Reads a window's client rectangle. Đọc hình chữ nhật vùng client của cửa sổ.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    /// <summary>Renders a window into the given device context. Vẽ cửa sổ vào device context đã cho.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);

    /// <summary>Gets the screen DC (pass zero for the whole screen). Lấy DC màn hình (truyền zero cho toàn màn hình).</summary>
    [DllImport("user32.dll")]
    internal static extern nint GetDC(nint hWnd);

    /// <summary>Releases a DC obtained via GetDC. Giải phóng DC đã lấy qua GetDC.</summary>
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(nint hWnd, nint hDC);

    /// <summary>Copies a rectangle of pixels from one DC to another. Copy một vùng pixel từ DC này sang DC khác.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        nint hdcDest, int xDest, int yDest, int wDest, int hDest,
        nint hdcSrc, int xSrc, int ySrc, uint rop);

    /// <summary>Creates a memory DC compatible with the given DC. Tạo memory DC tương thích với DC đã cho.</summary>
    [DllImport("gdi32.dll")]
    internal static extern nint CreateCompatibleDC(nint hdc);

    /// <summary>Creates a 32-bit DIB section and returns its bitmap handle plus a pointer to the bits. Tạo DIB section 32-bit, trả về handle bitmap và con trỏ tới bit điểm ảnh.</summary>
    [DllImport("gdi32.dll")]
    internal static extern nint CreateDIBSection(
        nint hdc, ref BITMAPINFO pbmi, uint iUsage, out nint ppvBits, nint hSection, uint dwOffset);

    /// <summary>Selects a GDI object into a DC. Chọn một đối tượng GDI vào DC.</summary>
    [DllImport("gdi32.dll")]
    internal static extern nint SelectObject(nint hdc, nint hgdiobj);

    /// <summary>Deletes a GDI bitmap/object. Xóa một bitmap/đối tượng GDI.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(nint hObject);

    /// <summary>Deletes a memory DC. Xóa một memory DC.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(nint hdc);

    /// <summary>Copies bitmap scanlines into a managed buffer. Chép các hàng scanline của bitmap vào buffer managed.</summary>
    [DllImport("gdi32.dll")]
    internal static extern int GetDIBits(
        nint hdc, nint hbmp, uint uStartScan, uint cScanLines,
        [Out] byte[] lpvBits, ref BITMAPINFO lpbmi, uint uUsage);

    /// <summary>Opts the process into the given DPI awareness context (best effort). Đăng ký nhận thức DPI cho tiến trình (best effort).</summary>
    [DllImport("user32.dll")]
    internal static extern nint SetProcessDpiAwarenessContext(nint dpiContext);

    /// <summary>Returns the DPI of the monitor hosting a window. Trả về DPI của màn hình chứa cửa sổ.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    // ---------------------------------------------------------------------------------------
    // DWM thumbnail fallback: some DirectX windows ignore WM_PRINT, so PrintWindow fails.
    // Rendering a DWM thumbnail of the source into a hidden helper window and printing that
    // helper recovers the live content without a graphics-capture hook.
    // Dự phòng DWM thumbnail: một số cửa sổ DirectX bỏ qua WM_PRINT nên PrintWindow thất bại.
    // Vẽ thumbnail DWM của nguồn vào một cửa sổ phụ ẩn rồi PrintWindow cửa sổ đó sẽ lấy lại
    // nội dung sống mà không cần hook capture đồ hoạ.
    // ---------------------------------------------------------------------------------------

    /// <summary>Popup window style (WS_POPUP). Kiểu cửa sổ popup (WS_POPUP).</summary>
    internal const int WS_POPUP = unchecked((int)0x80000000);

    /// <summary>Tool-window extended style: no taskbar entry (WS_EX_TOOLWINDOW). Kiểu mở rộng tool-window: không lên taskbar (WS_EX_TOOLWINDOW).</summary>
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Do not activate the window on show (WS_EX_NOACTIVATE). Không kích hoạt cửa sổ khi hiện (WS_EX_NOACTIVATE).</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Shows the window without activating it (SW_SHOWNA). Hiện cửa sổ không kích hoạt (SW_SHOWNA).</summary>
    internal const int SW_SHOWNA = 8;

    /// <summary>Thumbnail flag: rcDestination is valid. Cờ thumbnail: rcDestination hợp lệ.</summary>
    internal const int DWM_TNP_RECTDESTINATION = 0x00000001;

    /// <summary>Thumbnail flag: opacity is valid. Cờ thumbnail: opacity hợp lệ.</summary>
    internal const int DWM_TNP_OPACITY = 0x00000004;

    /// <summary>Thumbnail flag: fVisible is valid. Cờ thumbnail: fVisible hợp lệ.</summary>
    internal const int DWM_TNP_VISIBLE = 0x00000008;

    /// <summary>Thumbnail flag: fSourceClientAreaOnly is valid. Cờ thumbnail: fSourceClientAreaOnly hợp lệ.</summary>
    internal const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    /// <summary>
    /// DWM_THUMBNAIL_PROPERTIES; BOOL fields are 4-byte ints, matching the native layout
    /// (a C# <see langword="bool"/> would silently shrink the struct and corrupt the call).
    /// DWM_THUMBNAIL_PROPERTIES; trường BOOL là int 4 byte đúng bố cục native (dùng
    /// <see langword="bool"/> C# sẽ làm struct ngắn đi và hỏng lời gọi).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DWM_THUMBNAIL_PROPERTIES
    {
        internal int dwFlags;
        internal RECT rcDestination;
        internal RECT rcSource;
        internal byte opacity;
        internal int fVisible;
        internal int fSourceClientAreaOnly;
    }

    /// <summary>Creates a window of a system class (helper for the DWM thumbnail). Tạo cửa sổ thuộc lớp hệ thống (cửa sổ phụ cho DWM thumbnail).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    /// <summary>Shows/hides a window. Hiện/ẩn cửa sổ.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>Destroys a window. Huỷ cửa sổ.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint hWnd);

    /// <summary>Registers a thumbnail of the source window into the destination window. Đăng ký thumbnail của cửa sổ nguồn vào cửa sổ đích.</summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumb);

    /// <summary>Removes a registered thumbnail. Gỡ thumbnail đã đăng ký.</summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmUnregisterThumbnail(nint thumb);

    /// <summary>Updates thumbnail properties (destination rect, visibility, opacity). Cập nhật thuộc tính thumbnail (rect đích, hiển thị, độ mờ).</summary>
    [DllImport("dwmapi.dll")]
    internal static extern int DwmUpdateThumbnailProperties(nint thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}
