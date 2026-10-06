namespace ReTran.Core.Serve;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Builds stable translation-context keys from a monitor index or a window handle.
/// Xây key ngữ cảnh dịch thuật ổn định từ chỉ số màn hình hoặc handle cửa sổ.
/// </summary>
/// <remarks>
/// Keys follow the plan's content-context scheme: <c>screen:N</c> for full-screen
/// capture, <c>&lt;process&gt;.exe</c> for window/hook capture (see AGENTS.md Data &amp; Persistence).
/// Key theo scheme content-context của plan: <c>screen:N</c> cho chụp toàn màn hình,
/// <c>&lt;process&gt;.exe</c> cho chụp cửa sổ/hook (xem AGENTS.md Data &amp; Persistence).
/// </remarks>
public static class ContextKey
{
    /// <summary>
    /// Retrieves the process owning a window (win32: GetWindowThreadProcessId).
    /// Lấy tiến trình sở hữu cửa sổ (win32: GetWindowThreadProcessId).
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Returns the context key for full-screen capture of a monitor.
    /// Trả về key ngữ cảnh cho chụp toàn màn hình của một monitor.
    /// </summary>
    /// <param name="monitor">Zero-based monitor index. Chỉ số monitor (bắt đầu từ 0).</param>
    /// <returns>The key <c>screen:{monitor}</c>. Key <c>screen:{monitor}</c>.</returns>
    public static string FromMonitor(int monitor) => $"screen:{monitor}";

    /// <summary>
    /// Returns the context key for a window: its owning process name with a <c>.exe</c> suffix.
    /// Trả về key ngữ cảnh cho một cửa sổ: tên tiến trình sở hữu kèm hậu tố <c>.exe</c>.
    /// </summary>
    /// <param name="hwnd">Window handle; pass 0 for the "unknown" key. Handle cửa sổ; truyền 0 để nhận key "unknown"/>.</param>
    /// <returns>
    /// Lowercase process name such as <c>explorer.exe</c>, or <c>unknown</c> when the handle
    /// maps to no running process or the lookup fails. Tên tiến trình dạng chữ thường như
    /// <c>explorer.exe</c>, hoặc <c>unknown</c> khi handle không thuộc tiến trình đang chạy
    /// hoặc tra cứu thất bại.
    /// </returns>
    public static string FromHwnd(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return "unknown";
            }

            return (Process.GetProcessById((int)pid).ProcessName + ".exe").ToLowerInvariant();
        }
        catch (Exception)
        {
            // Any failure (dead/exited process, access denied, invalid handle) degrades to
            // "unknown" so the display path never crashes. Mọi lỗi (process đã tắt, từ chối
            // truy cập, handle không hợp lệ) đều hạ cấp xuống "unknown" để path hiển thị không crash.
            return "unknown";
        }
    }
}
