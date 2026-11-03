using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using ReTran_App.Services.Update;

namespace ReTran_App.Services.Window;

/// <summary>
/// WinUI glue for the primary app window: hooks <c>AppWindow.Closing</c> so pressing X hides the
/// window to the tray (the capture/OCR pipeline and core keep running) instead of quitting, and
/// exposes show/hide/exit primitives for tray actions. Not thread-safe — all members are called
/// from the UI thread.
/// Phần glue WinUI cho cửa sổ chính của app: hook <c>AppWindow.Closing</c> để bấm X ẩn cửa sổ xuống
/// khay (pipeline capture/OCR và core vẫn chạy) thay vì thoát, đồng thời cung cấp primitive
/// hiện/ẩn/thoát cho hành động khay. Không thread-safe — mọi thành viên gọi từ UI thread.
/// </summary>
public sealed class AppWindowBridge : IDisposable
{
    private readonly ToastService _toast;
    private readonly I18nService _L;
    private readonly UpdateCoordinator _coordinator;
    private readonly ILogger<AppWindowBridge> _log;

    private nint _hwnd;
    private Microsoft.UI.Xaml.Window? _winuiWindow;
    private AppWindow? _appWindow;
    private volatile bool _visible = true;
    private volatile bool _exiting;
    private bool _disposed;

    /// <summary>
    /// Creates the bridge; no window is hooked until <see cref="Attach"/> is called.
    /// Tạo bridge; chưa hook window nào cho tới khi <see cref="Attach"/> được gọi.
    /// </summary>
    /// <param name="toast">Toast bus for user-facing messages. Bus toast cho thông điệp tới người dùng.</param>
    /// <param name="L">Localized UI strings. Chuỗi UI đã dịch.</param>
    /// <param name="coordinator">Update state source read at Closing time. Nguồn trạng thái update đọc lúc Closing.</param>
    /// <param name="log">Reports a hook that could not be installed or a hide that failed. Báo cáo hook không cài được hoặc lần ẩn thất bại.</param>
    public AppWindowBridge(ToastService toast, I18nService L, UpdateCoordinator coordinator,
        ILogger<AppWindowBridge> log)
    {
        _toast = toast;
        _L = L;
        _coordinator = coordinator;
        _log = log;
    }

    /// <summary>Whether the primary window's Closing event has been hooked. Đã hook Closing của cửa sổ chính chưa.</summary>
    public bool IsAttached => _appWindow is not null;

    /// <summary>Whether the window has been shown and not hidden since. Window đã hiện và chưa bị ẩn kể từ đó.</summary>
    public bool IsVisible => _visible;

    /// <summary>Whether this app's window is the foreground window. Cửa sổ của app có đang là foreground window.</summary>
    public bool IsForeground => _hwnd != 0 && NativeMethods.GetForegroundWindow() == _hwnd;

    /// <summary>
    /// Resolves the AppWindow for <paramref name="winuiWindow"/> and hooks its Closing event.
    /// Call exactly once, for the primary window only (see <see cref="WindowHooks"/>). A no-op when
    /// the AppWindow cannot be resolved — the bridge then stays unattached and a later call can
    /// retry.
    /// Phân giải AppWindow của <paramref name="winuiWindow"/> và hook Closing của nó. Gọi đúng
    /// một lần, chỉ cho window chính (xem <see cref="WindowHooks"/>). Không làm gì khi không phân giải
    /// được AppWindow — bridge khi đó vẫn chưa attach và lần gọi sau thử lại được.
    /// </summary>
    /// <param name="winuiWindow">The WinUI window to hook. Cửa sổ WinUI cần hook.</param>
    public void Attach(Microsoft.UI.Xaml.Window winuiWindow)
    {
        if (_disposed || _appWindow is not null) return;

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winuiWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        AppWindow? appWindow = AppWindow.GetFromWindowId(windowId);
        if (appWindow is null)
        {
            // The WinRT projection declares the return type non-nullable, but GetFromWindowId does
            // hand back null when no AppWindow backs the id (e.g. the window is already gone).
            // Storing state BEFORE the check would leave a half-initialized bridge — _hwnd set, so
            // IsForeground answers, and a NullReferenceException on the Closing line. Skipping the
            // assignment keeps IsAttached false and lets a later Attach retry.
            // Projection WinRT khai báo kiểu trả về là non-nullable, nhưng GetFromWindowId vẫn trả
            // null khi id không có AppWindow đứng sau (vd. window đã đóng). Gán trạng thái TRƯỚC khi
            // kiểm tra sẽ để lại bridge khởi tạo dở: _hwnd đã gán nên IsForeground trả lời, và NRE
            // ngay ở dòng đăng ký Closing. Không gán giữ IsAttached bằng false và cho phép Attach
            // lần sau thử lại.
            _log.LogWarning(
                "AppWindow.GetFromWindowId returned no window for HWND 0x{Hwnd:X}; the close-to-tray hook stays off.",
                hwnd.ToInt64());
            return;
        }

        _hwnd = hwnd;
        _winuiWindow = winuiWindow;
        _appWindow = appWindow;
        _appWindow.Closing += OnClosing;
    }

    /// <summary>
    /// Hides the window to the tray without touching the pipeline or the core process.
    /// Ẩn cửa sổ xuống khay mà không đụng pipeline hay tiến trình core.
    /// </summary>
    public void HideToTray()
    {
        if (_appWindow is null) return;
        _appWindow.Hide();
        _visible = false;
    }

    /// <summary>
    /// Shows the window again and brings it to the foreground. Hiện cửa sổ trở lại và đưa lên trước.
    /// </summary>
    public void Restore()
    {
        if (_appWindow is null || _winuiWindow is null) return;
        _appWindow.Show();
        // AppWindow in WinAppSDK 1.7 has no Activate — activate the Xaml window (same result:
        // show + bring to foreground). AppWindow trong WinAppSDK 1.7 không có Activate — activate
        // cửa sổ Xaml (kết quả tương tự: hiện + đưa lên trước).
        _winuiWindow.Activate();
        _visible = true;
    }

    /// <summary>
    /// Marks a real app exit (idempotent) so subsequent Closing events allow the close instead of
    /// hiding to the tray. Call before <c>IAppShutdown.Quit()</c>.
    /// Đánh dấu app thoát thật (idempotent) để các sự kiện Closing sau đó cho phép đóng thay vì
    /// ẩn xuống khay. Gọi TRƯỚC <c>IAppShutdown.Quit()</c>.
    /// </summary>
    public void BeginExit() => _exiting = true;

    /// <summary>
    /// Detaches the Closing hook so a later shutdown is no longer intercepted.
    /// Gỡ hook Closing để lần shutdown sau không còn bị chặn.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_appWindow is not null)
        {
            _appWindow.Closing -= OnClosing;
            _appWindow = null;
        }
        _winuiWindow = null;
        // Clearing the HWND makes IsForeground stop answering after detach: the handle it would
        // compare against is no longer owned by this bridge, so AppNotifier must not keep routing
        // messages as if this window were still the app's window.
        // Xóa HWND khiến IsForeground ngừng trả lời sau khi detach: handle mà nó so sánh với
        // foreground không còn thuộc bridge này, nên AppNotifier không được tiếp tục định tuyến thông
        // điệp như thể đây vẫn là cửa sổ của app.
        _hwnd = 0;
    }

    // Cancelling Closing only prevents destruction — the window would stay visible — so the
    // hide-to-tray branch must hide it here as well (close-to-tray pattern in WinAppSDK).
    // Cancel Closing chỉ ngăn hủy cửa sổ — cửa sổ vẫn hiện — nên nhánh ẩn xuống khay phải tự
    // ẩn ngay tại đây (close-to-tray pattern trong WinAppSDK).
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (ClosePolicy.Decide(_coordinator.UpdateInProgress, _exiting) == CloseDecision.HideToTray)
        {
            e.Cancel = true;
            try
            {
                HideToTray();
            }
            catch (Exception ex)
            {
                // The close is already cancelled, so this cannot re-open it — but an exception escaping
                // a WinRT event handler runs on the window procedure's own call stack and would take
                // down the process that close-to-tray exists to keep alive. The window simply stays
                // visible; _visible still describes the last successful hide, so the tray keeps
                // offering OpenApp (Restore) as the way back.
                // Cú đóng đã bị cancel nên lỗi không mở lại nó được — nhưng exception thoát khỏi event
                // handler WinRT sẽ chạy ngay trên call stack của window procedure và giết chính process
                // mà close-to-tray sinh ra để giữ sống. Cửa sổ chỉ đơn giản là vẫn hiện; _visible vẫn mô
                // tả lần ẩn thành công gần nhất, nên khay vẫn đưa ra OpenApp (Restore) để mở lại.
                _log.LogError(ex,
                    "Hiding the primary window to the tray failed; the window stays visible and the app keeps running.");
            }
        }
    }

    /// <summary>
    /// Win32 entry point used by the bridge (internal only).
    /// Hàm Win32 mà bridge dùng (chỉ nội bộ).
    /// </summary>
    /// <remarks>
    /// Classic DllImport is used (not LibraryImport) so no project change
    /// (AllowUnsafeBlocks) is needed — same convention as ReTran.App.Services.OverlayWindow.Win32OverlayHost.
    /// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
    /// (AllowUnsafeBlocks) — cùng quy ước với ReTran.App.Services.OverlayWindow.Win32OverlayHost.
    /// </remarks>
    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}
