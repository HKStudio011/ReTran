namespace ReTran.Core.Capture;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Identifies a visible top-level window that can be captured.
/// Định danh một cửa sổ top-level đang hiển thị có thể chụp được.
/// </summary>
/// <param name="Hwnd">Window handle. Handle của cửa sổ.</param>
/// <param name="Title">Window title (never null/whitespace). Tiêu đề cửa sổ (không bao giờ null/rỗng).</param>
/// <param name="Width">Client width in physical pixels (0 when unreadable). Chiều rộng vùng client theo pixel vật lý (0 khi không đọc được).</param>
/// <param name="Height">Client height in physical pixels (0 when unreadable). Chiều cao vùng client theo pixel vật lý (0 khi không đọc được).</param>
public sealed record WindowInfo(nint Hwnd, string Title, int Width, int Height);

/// <summary>
/// Per-window capture source backed by PrintWindow/BitBlt GDI on a dedicated background thread.
/// Nguồn chụp từng cửa sổ dùng PrintWindow/BitBlt GDI trên một luồng nền riêng.
/// </summary>
public class WindowCaptureSource : ICaptureSource
{
    private readonly WindowTarget _target;
    private readonly ILogger? _logger;
    private readonly int _fps;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    private static int s_dpiAwarenessAttempted;

    /// <summary>
    /// Creates a window capture source for the window identified by <paramref name="target"/>.Hwnd.
    /// Tạo nguồn chụp cửa sổ cho cửa sổ được xác định bởi <paramref name="target"/>.Hwnd.
    /// </summary>
    /// <param name="target">The window to capture. Cửa sổ cần chụp.</param>
    /// <param name="logger">Optional logger; all diagnostics go through it (never stdout — core stdout is JSON-RPC). Logger tùy chọn; mọi chẩn đoán qua đây (không bao giờ stdout — stdout của core là JSON-RPC).</param>
    /// <param name="fps">Target capture cadence in frames per second (>= 1). Nhịp chụp mục tiêu (frame/giây, >= 1).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when fps &lt; 1.</exception>
    public WindowCaptureSource(WindowTarget target, ILogger? logger, int fps = 10)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _logger = logger;
        if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps), "fps must be >= 1.");
        _fps = fps;
    }

    /// <summary>What this source captures. Nguồn này chụp cái gì.</summary>
    public CaptureTarget Target => _target;

    /// <summary>Raised on the capture thread for each captured frame (ownership of the frame transfers to the handler). Kích hoạt trên luồng chụp cho mỗi frame (handler nhận quyền sở hữu frame).</summary>
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;

    /// <summary>Whether capture is currently running. Có đang chạy chụp hay không.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Starts the dedicated background capture thread and begins capturing at the configured fps.
    /// Khởi động luồng nền chụp riêng và bắt đầu chụp theo fps đã cấu hình.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the source has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already running.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("Capture is already running.");

        // DPI: opt the process into Per-Monitor-V2 once (best effort) before any
        // sizing call, so GetClientRect/DIB dimensions are in physical pixels.
        // DPI: đăng ký Per-Monitor-V2 một lần (best effort) trước mọi phép đo kích
        // thước, để GetClientRect/kích thước DIB tính theo pixel vật lý.
        EnsureDpiAwareness();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _running = true;
        _thread = new Thread(() => CaptureLoop(token))
        {
            IsBackground = true,
            Name = "ReTran.WindowCapture"
        };
        // GDI does not require MTA, but keep the same pattern as ScreenCaptureSource
        // (one dedicated background thread, never the JSON-RPC loop thread).
        // GDI không bắt buộc MTA, nhưng giữ cùng mẫu với ScreenCaptureSource
        // (một luồng nền riêng, không bao giờ dùng luồng JSON-RPC).
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// Stops capture: cancels the loop and waits for the thread to exit.
    /// Dừng chụp: hủy vòng lặp và chờ luồng thoát.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _cts?.Cancel();
        var thread = _thread;
        if (thread is not null && !thread.Join(7000))
            LogWarning("Capture thread did not exit within 7s of Stop().");
        _thread = null;
        _running = false;
    }

    /// <summary>Stops capture and releases all resources. Dừng chụp và giải phóng mọi tài nguyên.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Pure filter: a window is capturable only when it is visible and has a non-blank title.
    /// Bộ lọc thuần túy: cửa sổ chỉ chụp được khi đang hiển thị và có tiêu đề không rỗng.
    /// </summary>
    /// <param name="title">Window title (may be null). Tiêu đề cửa sổ (có thể null).</param>
    /// <param name="isVisible">Whether the window is visible. Cửa sổ có đang hiển thị hay không.</param>
    /// <returns>True when visible and titled; false otherwise. True khi hiển thị và có tiêu đề; ngược lại false.</returns>
    public static bool IsCapturable(string? title, bool isVisible) =>
        isVisible && !string.IsNullOrWhiteSpace(title);

    /// <summary>
    /// Enumerates all visible top-level windows with a non-empty title via EnumWindows.
    /// Liệt kê mọi cửa sổ top-level đang hiển thị có tiêu đề qua EnumWindows.
    /// </summary>
    /// <returns>One entry per capturable window (Hwnd, Title, client Width/Height; 0x0 when the client rect is unreadable — still listed, capture skips later). Một mục cho mỗi cửa sổ chụp được (Hwnd, Title, Width/Height vùng client; 0x0 khi không đọc được rect — vẫn liệt kê, lúc chụp sẽ bỏ qua).</returns>
    /// <exception cref="InvalidOperationException">Thrown when enumeration fails unexpectedly.</exception>
    public static IReadOnlyList<WindowInfo> EnumerateWindows()
    {
        EnsureDpiAwareness();

        var result = new List<WindowInfo>();
        bool Callback(nint hWnd, nint _)
        {
            bool visible;
            try { visible = NativeMethods.IsWindowVisible(hWnd); }
            catch (DllNotFoundException) { throw; }
            if (!visible) return true;

            string title;
            try { title = GetTitle(hWnd); }
            catch (DllNotFoundException) { throw; }
            if (!IsCapturable(title, true)) return true;

            int width = 0, height = 0;
            try
            {
                if (NativeMethods.GetClientRect(hWnd, out var rect))
                {
                    width = Math.Max(0, rect.Right - rect.Left);
                    height = Math.Max(0, rect.Bottom - rect.Top);
                }
            }
            catch (DllNotFoundException) { throw; }
            // Unreadable rect → still listed as 0x0; CaptureOnce skips it later.
            // Không đọc được rect → vẫn liệt kê dạng 0x0; CaptureOnce sẽ bỏ qua sau.
            result.Add(new WindowInfo(hWnd, title, width, height));
            return true;
        }

        NativeMethods.EnumWindowsProc proc = Callback;
        try
        {
            NativeMethods.EnumWindows(proc, nint.Zero);
        }
        finally
        {
            GC.KeepAlive(proc); // keep the delegate alive for the P/Invoke call. Giữ delegate sống trong suốt cuộc gọi P/Invoke.
        }
        return result;
    }

    /// <summary>
    /// Capture thread body: on a 1000/fps cadence — CaptureOnce → FrameArrived; never throws out of the loop.
    /// Thân luồng chụp: theo nhịp 1000/fps ms — CaptureOnce → FrameArrived; không bao giờ ném ngoại lệ ra khỏi vòng lặp.
    /// </summary>
    private void CaptureLoop(CancellationToken token)
    {
        int periodMs = Math.Max(1, 1000 / _fps);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    CaptureOnce();
                }
                catch (Exception ex)
                {
                    // Never let the thread die silently — log and keep the cadence.
                    // Không bao giờ để luồng chết im lặng — log rồi giữ nhịp chụp.
                    LogError("Unexpected window capture error: {Message}", ex.Message);
                }

                var elapsed = (int)sw.ElapsedMilliseconds;
                if (!token.IsCancellationRequested && elapsed < periodMs)
                    InterruptibleSleep(periodMs - elapsed, token);
            }
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// One capture attempt: sizes via GetClientRect (0-size → log + skip, no throw), renders via PrintWindow
    /// into a 32-bit top-down DIB, reads back with GetDIBits honoring stride, and raises FrameArrived.
    /// Một lần thử chụp: đo kích thước qua GetClientRect (0-size → log + bỏ qua, không throw), vẽ qua PrintWindow
    /// vào DIB 32-bit top-down, đọc lại bằng GetDIBits theo stride, rồi kích hoạt FrameArrived.
    /// </summary>
    /// <returns>True when a frame was raised; false when skipped. True khi đã phát frame; false khi bỏ qua.</returns>
    private bool CaptureOnce()
    {
        nint hwnd = _target.Hwnd;

        if (!NativeMethods.GetClientRect(hwnd, out var rect))
        {
            LogWarning("GetClientRect failed for HWND {Hwnd} — invalid handle, frame skipped.", hwnd);
            return false;
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            LogWarning("Window HWND {Hwnd} has empty client area ({Width}x{Height}) — frame skipped.", hwnd, width, height);
            return false;
        }

        // DIB size is in physical pixels (process opted into Per-Monitor-V2 in Start()).
        // Kích thước DIB tính theo pixel vật lý (tiến trình đã đăng ký Per-Monitor-V2 trong Start()).
        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down: rows run top to bottom, no vertical flip needed. Top-down: các hàng từ trên xuống, khỏi lật dọc.
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        // GDI discipline: every DC/bitmap created below is deleted on this same
        // thread in finally blocks — no handle leaks across frames.
        // Kỷ luật GDI: mọi DC/bitmap tạo bên dưới đều bị xóa ngay trên luồng này
        // trong khối finally — không rò rỉ handle giữa các frame.
        nint hdcScreen = NativeMethods.GetDC(nint.Zero);
        if (hdcScreen == nint.Zero)
        {
            LogError("GetDC failed — frame skipped.");
            return false;
        }
        try
        {
            nint hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
            if (hdcMem == nint.Zero)
            {
                LogError("CreateCompatibleDC failed — frame skipped.");
                return false;
            }
            try
            {
                nint hBmp = NativeMethods.CreateDIBSection(
                    hdcMem, ref bmi, NativeMethods.DIB_RGB_COLORS, out _, nint.Zero, 0);
                if (hBmp == nint.Zero)
                {
                    LogError("CreateDIBSection failed for {Width}x{Height} — frame skipped.", width, height);
                    return false;
                }
                try
                {
                    nint hOld = NativeMethods.SelectObject(hdcMem, hBmp);
                    try
                    {
                        bool ok = NativeMethods.PrintWindow(hwnd, hdcMem, NativeMethods.PW_RENDERFULLCONTENT);
                        if (!ok)
                            ok = NativeMethods.PrintWindow(hwnd, hdcMem, 0); // retry with legacy flag 0. Thử lại với cờ legacy 0.
                        if (!ok)
                        {
                            LogWarning("PrintWindow failed for HWND {Hwnd} — frame skipped.", hwnd);
                            return false;
                        }

                        // Top-down DIB (negative biHeight): GetDIBits returns rows in
                        // top-to-bottom order, but honor the stride row by row anyway.
                        // DIB top-down (biHeight âm): GetDIBits trả các hàng từ trên xuống,
                        // nhưng vẫn copy từng hàng theo stride cho chắc.
                        var raw = new byte[width * height * 4];
                        int lines = NativeMethods.GetDIBits(
                            hdcMem, hBmp, 0, (uint)height, raw, ref bmi, NativeMethods.DIB_RGB_COLORS);
                        if (lines == 0)
                        {
                            LogWarning("GetDIBits returned 0 lines for HWND {Hwnd} — frame skipped.", hwnd);
                            return false;
                        }

                        var pixels = new byte[width * height * 4];
                        int rowBytes = width * 4;
                        for (int y = 0; y < height; y++)
                            Buffer.BlockCopy(raw, y * rowBytes, pixels, y * rowBytes, rowBytes);

                        var frame = Frame.FromBgra(width, height, pixels);
                        FrameArrived?.Invoke(this, new FrameArrivedEventArgs(frame));
                        return true;
                    }
                    finally
                    {
                        NativeMethods.SelectObject(hdcMem, hOld);
                    }
                }
                finally
                {
                    NativeMethods.DeleteObject(hBmp);
                }
            }
            finally
            {
                NativeMethods.DeleteDC(hdcMem);
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    /// <summary>Reads a window's title (empty when unreadable). Đọc tiêu đề cửa sổ (rỗng khi không đọc được).</summary>
    private static string GetTitle(nint hWnd)
    {
        const int capacity = 512;
        var sb = new StringBuilder(capacity);
        int len = NativeMethods.GetWindowTextW(hWnd, sb, capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// Opts the process into Per-Monitor-V2 DPI awareness once (best effort: failures are swallowed
    /// so non-Windows/headless callers still work). Must run before any sizing call.
    /// Đăng ký nhận thức DPI Per-Monitor-V2 một lần (best effort: nuốt lỗi để môi trường
    /// non-Windows/headless vẫn chạy được). Phải chạy trước mọi phép đo kích thước.
    /// </summary>
    private static void EnsureDpiAwareness()
    {
        if (Interlocked.Exchange(ref s_dpiAwarenessAttempted, 1) != 0) return;
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(
                NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (DllNotFoundException) { /* non-Windows: ignore. Ngoài Windows: bỏ qua. */ }
        catch (EntryPointNotFoundException) { /* pre-1703 Windows: ignore. Windows cũ: bỏ qua. */ }
    }

    /// <summary>Sleeps up to <paramref name="ms"/> but returns immediately on cancellation (polls every 50 ms). Ngủ tối đa ms nhưng thoát ngay khi bị hủy (kiểm tra mỗi 50 ms).</summary>
    private static void InterruptibleSleep(int ms, CancellationToken token)
    {
        int remaining = ms;
        while (remaining > 0 && !token.IsCancellationRequested)
        {
            int chunk = Math.Min(remaining, 50);
            Thread.Sleep(chunk);
            remaining -= chunk;
        }
    }

    private void LogError(string message, params object[] args) => _logger?.LogError(message, args);
    private void LogWarning(string message, params object[] args) => _logger?.LogWarning(message, args);
}
