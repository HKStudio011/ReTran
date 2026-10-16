using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SD = System.Drawing;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Win32 implementation of <see cref="IOverlayHost"/>: a layered (per-pixel alpha), topmost,
/// click-through, non-activating popup window owned by a dedicated STA worker thread with its
/// own message pump. All public members enqueue work onto that thread, so the host is
/// thread-safe and never blocks callers.
/// Implementation Win32 của <see cref="IOverlayHost"/>: cửa sổ popup layered (alpha theo pixel),
/// luôn trên cùng, click-through, không kích hoạt, thuộc về một thread STA riêng có message
/// pump riêng. Mọi thành viên public enqueue lên thread đó, nên host thread-safe và không bao
/// giờ chặn người gọi.
/// </summary>
public sealed class Win32OverlayHost : IOverlayHost
{
    private const string WindowClassName = "ReTranOverlay";

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _thread;
    private nint _hwnd;
    private volatile bool _created;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private int _started;

    /// <summary>
    /// Whether the native window has been created on the worker thread. Native window đã được tạo trên worker thread hay chưa.
    /// </summary>
    public bool IsCreated => _created;

    /// <summary>
    /// Starts the worker thread and creates the native window (idempotent, waits up to 5 s).
    /// Khởi chạy worker thread và tạo native window (idempotent, chờ tối đa 5 giây).
    /// </summary>
    public void Create()
    {
        if (_disposed) return;
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 1) return;

        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() =>
        {
            try
            {
                CreateWindowOnWorker();
            }
            finally
            {
                try { done.Set(); }
                catch (ObjectDisposedException) { } // Create() gave up waiting — ignore.
            }
        });

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ReTranOverlayHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        done.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Moves/resizes the window to the given physical-pixel rectangle, keeps it topmost and
    /// shows it without activating it. Di chuyển/đổi cỡ cửa sổ về hình chữ nhật pixel vật lý
    /// cho trước, giữ trên cùng và hiện mà không kích hoạt nó.
    /// </summary>
    /// <param name="x">Left edge in physical pixels. Mép trái theo pixel vật lý.</param>
    /// <param name="y">Top edge in physical pixels. Mép trên theo pixel vật lý.</param>
    /// <param name="width">Width in physical pixels. Chiều rộng theo pixel vật lý.</param>
    /// <param name="height">Height in physical pixels. Chiều cao theo pixel vật lý.</param>
    public void Show(int x, int y, int width, int height) => Enqueue(() =>
    {
        if (_hwnd == 0) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    });

    /// <summary>
    /// Hides the window without destroying it. Ẩn cửa sổ mà không hủy nó.
    /// </summary>
    public void Hide() => Enqueue(() =>
    {
        if (_hwnd != 0) NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
    });

    /// <summary>
    /// Renders the boxes onto a 32-bit premultiplied bitmap and pushes it to the window with
    /// UpdateLayeredWindow; an empty list leaves the window fully transparent.
    /// Render các hộp lên bitmap 32-bit premultiplied và đẩy vào cửa sổ bằng
    /// UpdateLayeredWindow; danh sách rỗng để cửa sổ trong suốt hoàn toàn.
    /// </summary>
    /// <param name="boxes">Boxes in viewport pixel coordinates. Các hộp theo tọa độ pixel viewport.</param>
    /// <param name="fontScale">Font size multiplier for overlay text. Hệ số cỡ chữ overlay.</param>
    public void Draw(IReadOnlyList<DrawBox> boxes, double fontScale) => Enqueue(() => DrawOnWorker(boxes, fontScale));

    /// <summary>
    /// Stops the message pump, destroys the native window and joins the worker thread
    /// (timeout 5 s). Dừng message pump, hủy native window và join worker thread (timeout 5 giây).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopRequested = true;
        _queue.Enqueue(() =>
        {
            if (_hwnd != 0)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = 0;
            }
            NativeMethods.PostQuitMessage(0);
        });
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _wake.Dispose();
    }

    private void Enqueue(Action action)
    {
        if (_disposed) return;
        _queue.Enqueue(action);
        _wake.Set();
    }

    private void Pump()
    {
        while (true)
        {
            while (_queue.TryDequeue(out Action? action))
            {
                try { action(); }
                catch (Exception ex)
                {
                    // The pump must never die; a failed action only skips one frame.
                    // Pump không bao giờ được chết; một action hỏng chỉ bỏ qua một frame.
                    Debug.WriteLine($"ReTranOverlayHost action failed: {ex}");
                }
            }
            if (_stopRequested) break;

            while (NativeMethods.PeekMessage(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
            {
                if (msg.message == NativeMethods.WM_QUIT)
                {
                    _stopRequested = true;
                    break;
                }
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            if (_stopRequested) break;

            _wake.WaitOne(100);
            _wake.Reset();
        }
    }

    private void CreateWindowOnWorker()
    {
        if (_hwnd != 0)
        {
            _created = true;
            return;
        }

        nint hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = WndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName,
        };
        NativeMethods.RegisterClassEx(ref wc); // already registered → 0, fine

        _hwnd = NativeMethods.CreateWindowExW(
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED
                | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE,
            WindowClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        _created = _hwnd != 0;
    }

    private void DrawOnWorker(IReadOnlyList<DrawBox> boxes, double fontScale)
    {
        if (_hwnd == 0) return;
        if (!NativeMethods.GetClientRect(_hwnd, out NativeMethods.RECT rect)) return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;

        using (var bitmap = new SD.Bitmap(width, height, SD.Imaging.PixelFormat.Format32bppPArgb))
        {
            using (SD.Graphics g = SD.Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = SD.Text.TextRenderingHint.AntiAlias;
                g.Clear(SD.Color.Transparent);

                float fontSize = Math.Clamp(width * 0.024f, 14f, 32f) * (float)fontScale;
                using var font = new SD.Font("Segoe UI", fontSize, SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
                using var fill = new SD.SolidBrush(SD.Color.FromArgb(115, 0, 0, 0));
                using var border = new SD.Pen(SD.Color.FromArgb(255, 34, 197, 94), 3f);
                using var textBrush = new SD.SolidBrush(SD.Color.White);

                foreach (DrawBox box in boxes)
                {
                    var rectF = new SD.RectangleF((float)box.X, (float)box.Y, (float)box.W, (float)box.H);
                    if (rectF.Width <= 0 || rectF.Height <= 0) continue;

                    using SD.Drawing2D.GraphicsPath path = GetRoundedRect(rectF, 6f);
                    g.FillPath(fill, path);
                    g.DrawPath(border, path);

                    SD.RectangleF textRect = SD.RectangleF.Inflate(rectF, -8f, -4f);
                    if (textRect.Width <= 0 || textRect.Height <= 0) continue;

                    using var fmt = (SD.StringFormat)SD.StringFormat.GenericTypographic.Clone();
                    fmt.LineAlignment = SD.StringAlignment.Center;
                    if (box.IsPanelLine)
                    {
                        fmt.Trimming = SD.StringTrimming.EllipsisCharacter;
                        fmt.FormatFlags |= SD.StringFormatFlags.NoWrap;
                    }
                    g.DrawString(box.Text, font, textBrush, textRect, fmt);
                }
            }

            PushBitmap(bitmap, width, height);
        }
    }

    // Draws the bitmap's premultiplied bits onto the window; pptDst is null so the position
    // previously set by SetWindowPos is kept.
    // Vẽ các bit premultiplied của bitmap lên cửa sổ; pptDst là null để giữ vị trí SetWindowPos đặt trước đó.
    private void PushBitmap(SD.Bitmap bitmap, int width, int height)
    {
        nint screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == 0) return;

        nint memDc = 0, hBitmap = 0, oldBitmap = 0;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == 0) return;

            hBitmap = bitmap.GetHbitmap(SD.Color.FromArgb(0, 0, 0, 0));
            oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

            var size = new NativeMethods.SIZE { cx = width, cy = height };
            var pointSource = new NativeMethods.POINT { x = 0, y = 0 };
            var blend = new NativeMethods.BLENDFUNCTION
            {
                BlendOp = NativeMethods.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = NativeMethods.AC_SRC_ALPHA, // premultiplied per-pixel alpha
            };
            NativeMethods.UpdateLayeredWindow(_hwnd, screenDc, IntPtr.Zero, ref size, memDc,
                ref pointSource, 0, ref blend, NativeMethods.ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != 0) NativeMethods.SelectObject(memDc, oldBitmap);
            if (hBitmap != 0) NativeMethods.DeleteObject(hBitmap);
            if (memDc != 0) NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static SD.Drawing2D.GraphicsPath GetRoundedRect(SD.RectangleF rect, float radius)
    {
        float d = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2) * 2;
        var path = new SD.Drawing2D.GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(rect);
            path.CloseFigure();
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Layered windows paint via UpdateLayeredWindow: never erase the background.
    // Cửa sổ layered vẽ qua UpdateLayeredWindow: không bao giờ xóa nền.
    private static nint StaticWndProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        msg == NativeMethods.WM_ERASEBKGND
            ? IntPtr.Zero
            : NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    // Roots the delegate so the native callback cannot be collected while the window lives.
    // Giữ delegate sống để callback native không bị thu hồi khi cửa sổ còn tồn tại.
    private static readonly NativeMethods.WNDPROC WndProcDelegate = StaticWndProc;

    /// <summary>
    /// Win32 entry points used by the host (internal only).
    /// Các hàm Win32 mà host dùng (chỉ nội bộ).
    /// </summary>
    /// <remarks>
    /// Classic DllImport is used (not LibraryImport) so no project change
    /// (AllowUnsafeBlocks) is needed — same convention as ReTran.Core.Capture.NativeMethods.
    /// Dùng DllImport cổ điển (không phải LibraryImport) để khỏi phải sửa project
    /// (AllowUnsafeBlocks) — cùng quy ước với ReTran.Core.Capture.NativeMethods.
    /// </remarks>
    private static class NativeMethods
    {
        internal const uint WS_POPUP = 0x8000_0000;
        internal const uint WS_EX_TRANSPARENT = 0x0000_0020;
        internal const uint WS_EX_TOPMOST = 0x0000_0008;
        internal const uint WS_EX_TOOLWINDOW = 0x0000_0080;
        internal const uint WS_EX_LAYERED = 0x0008_0000;
        internal const uint WS_EX_NOACTIVATE = 0x0800_0000;

        internal const int SW_HIDE = 0;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal static readonly nint HWND_TOPMOST = new(-1);

        internal const uint WM_QUIT = 0x0012;
        internal const uint WM_ERASEBKGND = 0x0014;
        internal const uint PM_REMOVE = 0x0001;

        // ULW_ALPHA (not ULW_OPAQUE): the overlay is per-pixel transparent over the game.
        // ULW_ALPHA (không phải ULW_OPAQUE): overlay trong suốt theo từng pixel đè lên game.
        internal const uint ULW_ALPHA = 0x0000_0002;
        internal const byte AC_SRC_OVER = 0x00;
        internal const byte AC_SRC_ALPHA = 0x01;

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

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int x;
            internal int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            internal int cx;
            internal int cy;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BLENDFUNCTION
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowExW(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(nint hWnd, int cmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint hWnd, out RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out MSG msg, nint hWnd, uint filterMin, uint filterMax, uint removeMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern nint DispatchMessage(ref MSG msg);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll")]
        internal static extern nint GetDC(nint hWnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(nint hWnd, nint hdc);

        [DllImport("gdi32.dll")]
        internal static extern nint CreateCompatibleDC(nint hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteDC(nint hdc);

        [DllImport("gdi32.dll")]
        internal static extern nint SelectObject(nint hdc, nint hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteObject(nint hObject);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            nint hwnd, nint hdcDst, nint pptDst, ref SIZE size, nint hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION blend, uint flags);
    }
}
