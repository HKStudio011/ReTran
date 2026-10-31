using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Win32 implementation of <see cref="IForegroundWatcher"/>: a dedicated worker thread with its
/// own message pump registers <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c> and reports the
/// window that is becoming foreground, so the overlay hides/shows without waiting for the anchor
/// poll.
/// Implementation Win32 của <see cref="IForegroundWatcher"/>: một worker thread riêng có message
/// pump riêng đăng ký <c>SetWinEventHook(EVENT_SYSTEM_FOREGROUND)</c> và báo cửa sổ đang trở
/// thành foreground, để overlay ẩn/hiện ngay mà không chờ poll anchor.
/// </summary>
/// <remarks>
/// The callback receives the event's window handle instead of calling
/// <c>GetForegroundWindow</c>, because the event can fire before the foreground actually changes.
/// Callback nhận handle từ event thay vì gọi <c>GetForegroundWindow</c>, vì event có thể bắn
/// trước khi foreground thực sự đổi.
/// </remarks>
public sealed class Win32ForegroundWatcher : IForegroundWatcher
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint WM_QUIT = 0x0012;

    private readonly ILogger<Win32ForegroundWatcher> _log;
    private readonly object _gate = new();

    private Action<nint>? _callback;
    private Thread? _thread;
    private uint _threadId;
    private bool _started;
    private volatile bool _disposed;

    /// <summary>
    /// Creates the watcher; call <see cref="Start"/> to install the hook.
    /// Tạo watcher; gọi <see cref="Start"/> để cài hook.
    /// </summary>
    /// <param name="log">Logger. Bộ ghi log.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="log"/> is null. Ném khi <paramref name="log"/> là null.</exception>
    public Win32ForegroundWatcher(ILogger<Win32ForegroundWatcher> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Waits up to two seconds for the hook to install; if installation fails the watcher stays
    /// silent and the overlay falls back to the 250 ms anchor poll.
    /// Chờ tối đa hai giây để cài hook; nếu cài thất bại watcher im lặng và overlay quay về poll
    /// anchor 250 ms.
    /// </remarks>
    public void Start(Action<nint> onForegroundChanged)
    {
        ArgumentNullException.ThrowIfNull(onForegroundChanged);

        Thread thread;
        var ready = new ManualResetEventSlim(false);

        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _callback = onForegroundChanged;

            thread = new Thread(() => Run(ready))
            {
                IsBackground = true,
                Name = "ReTranForegroundWatcher",
            };
            _thread = thread;
        }

        // Never wait for the hook while holding the gate: the worker needs it to publish its
        // thread id, so waiting under the lock would always time out.
        // Không chờ hook trong lúc giữ lock: worker cần lock để công bố thread id, nếu giữ lock
        // thì lần chờ nào cũng hết timeout.
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Posts <c>WM_QUIT</c> to the worker thread, unhooks the WinEvent hook and joins the thread
    /// (timeout 2 s). Gửi <c>WM_QUIT</c> cho worker thread, gỡ hook WinEvent và join thread
    /// (timeout 2 giây).
    /// </summary>
    public void Dispose()
    {
        Thread? thread;
        uint threadId;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _callback = null;
            thread = _thread;
            threadId = _threadId;
        }

        if (threadId != 0) NativeMethods.PostThreadMessageW(threadId, WM_QUIT, 0, 0);
        // Join only a running thread: Start may not have called Thread.Start yet (no-op then).
        // Chỉ join thread đang chạy: Start có thể chưa gọi Thread.Start (khi đó bỏ qua).
        if (thread is { IsAlive: true }) thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run(ManualResetEventSlim ready)
    {
        NativeMethods.WinEventProc callback = OnWinEvent;
        nint hook = 0;

        try
        {
            // Creating the queue before the thread id is read makes PostThreadMessage reliable.
            // Tạo message queue trước khi đọc thread id để PostThreadMessage luôn đáng tin.
            _ = NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
            lock (_gate)
            {
                // Disposed while the thread was starting: do not install a hook nobody can unhook.
                // Bị dispose trong lúc thread đang khởi động: không cài hook mà không ai gỡ được.
                if (_disposed) return;
                _threadId = NativeMethods.GetCurrentThreadId();
            }

            hook = NativeMethods.SetWinEventHook(
                EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, callback, 0, 0,
                WINEVENT_OUTOFCONTEXT);
            if (hook == 0)
            {
                _log.LogWarning(
                    "SetWinEventHook(EVENT_SYSTEM_FOREGROUND) failed; overlay falls back to the anchor poll.");
                return;
            }

            ready.Set();

            while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Foreground watcher stopped unexpectedly; overlay falls back to the anchor poll.");
        }
        finally
        {
            if (hook != 0) NativeMethods.UnhookWinEvent(hook);
            ready.Set();
            GC.KeepAlive(callback);
        }
    }

    private void OnWinEvent(
        nint hook, uint eventId, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        Action<nint>? callback = _callback;
        if (_disposed || callback is null || hwnd == 0) return;

        try
        {
            callback(hwnd);
        }
        catch (Exception ex)
        {
            // The pump must never die; a failed callback only skips one foreground change.
            // Pump không bao giờ được chết; một callback hỏng chỉ bỏ qua một lần đổi foreground.
            _log.LogWarning(ex, "Foreground callback failed.");
        }
    }

    /// <summary>
    /// Win32 entry points used by the watcher (internal only).
    /// Các hàm Win32 mà watcher dùng (chỉ nội bộ).
    /// </summary>
    /// <remarks>
    /// Classic DllImport is used (not LibraryImport) so no project change
    /// (AllowUnsafeBlocks) is needed — same convention as ReTran.Core.Capture.NativeMethods.
    /// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
    /// (AllowUnsafeBlocks) — cùng quy ước với ReTran.Core.Capture.NativeMethods.
    /// </remarks>
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int x;
            internal int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            internal nint hwnd;
            internal uint message;
            internal nuint wParam;
            internal nint lParam;
            internal uint time;
            internal POINT pt;
        }

        internal delegate void WinEventProc(
            nint hWinEventHook, uint eventId, nint hwnd, int idObject, int idChild,
            uint idEventThread, uint msTime);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetMessageW(out MSG msg, nint hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessageW(uint threadId, uint msg, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        internal static extern nint SetWinEventHook(
            uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc callback,
            uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(nint hWinEventHook);
    }
}
