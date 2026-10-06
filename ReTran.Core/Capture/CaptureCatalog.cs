namespace ReTran.Core.Capture;

/// <summary>
/// Read-only enumeration of capture sources shared by the <c>capture</c> CLI and serve mode
/// (<c>core.list_sources</c>) — data only, never prints.
/// Liệt kê nguồn chụp chỉ-đọc, dùng chung giữa CLI <c>capture</c> và serve mode
/// (<c>core.list_sources</c>) — chỉ dữ liệu, không in ra.
/// </summary>
public static class CaptureCatalog
{
    /// <summary>
    /// Lists all attached monitors (index 0 is the primary).
    /// Liệt kê mọi monitor đang gắn (chỉ số 0 là màn hình chính).
    /// </summary>
    /// <returns>One entry per attached monitor. Một mục cho mỗi monitor đang gắn.</returns>
    /// <exception cref="InvalidOperationException">Thrown when DXGI reports no attached outputs. Khi DXGI không báo output nào đang gắn.</exception>
    public static IReadOnlyList<MonitorInfo> ListMonitors() => ScreenCaptureSource.EnumerateMonitors();

    /// <summary>
    /// Lists visible capturable top-level windows.
    /// Liệt kê các cửa sổ cấp cao nhìn thấy được và chụp được.
    /// </summary>
    /// <returns>One entry per capturable window. Một mục cho mỗi cửa sổ chụp được.</returns>
    /// <exception cref="InvalidOperationException">Thrown when enumeration fails unexpectedly. Khi liệt kê thất bại ngoài dự kiến.</exception>
    public static IReadOnlyList<WindowInfo> ListWindows() => WindowCaptureSource.EnumerateWindows();
}
