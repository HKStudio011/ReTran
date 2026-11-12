using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ReTran_App.Services.FloatingMenu;
using SD = System.Drawing;

namespace ReTran_App.Services.OverlayWindow;

/// <summary>
/// Win32 implementation of <see cref="IOverlayHost"/>: a layered (per-pixel alpha), topmost,
/// click-through, non-activating popup window owned by a dedicated STA worker thread with its
/// own message pump. All public members enqueue work onto that thread, so the host is
/// thread-safe and never blocks callers. Also implements <see cref="IMenuWindowHost"/>: a second
/// layered, topmost, NON click-through, non-activating popup ("ReTranMenu") on the SAME worker
/// thread/pump, used by the floating menu (drag, panel clicks, shadow/corner hit-testing).
/// Implementation Win32 của <see cref="IOverlayHost"/>: cửa sổ popup layered (alpha theo pixel),
/// luôn trên cùng, click-through, không kích hoạt, thuộc về một thread STA riêng có message
/// pump riêng. Mọi thành viên public enqueue lên thread đó, nên host thread-safe và không bao
/// giờ chặn người gọi. Đồng thời implement <see cref="IMenuWindowHost"/>: cửa sổ popup thứ hai
/// layered, topmost, KHÔNG click-through, không kích hoạt ("ReTranMenu") chạy trên CÙNG worker
/// thread/pump, dùng cho menu nổi (kéo, click panel, hit-test vùng shadow/góc bo tròn).
/// </summary>
public sealed class Win32OverlayHost : IOverlayHost, IMenuWindowHost
{
    private const string WindowClassName = "ReTranOverlay";
    private const string MenuWindowClassName = "ReTranMenu";

    /// <summary>Press displacement (px) beyond which a panel press becomes a drag. Lệch nhấn (px) vượt qua thì chuyển từ click sang kéo.</summary>
    private const int DragThreshold = 4;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _thread;
    private nint _hwnd;
    private volatile bool _created;
    private volatile bool _stopRequested;
    private volatile bool _disposed;
    private int _started;

    // Menu window state (worker thread only, except the volatile hwnd/created flags).
    // Trạng thái cửa sổ menu (chỉ worker thread, trừ flag hwnd/created kiểu volatile).
    private nint _menuHwnd;
    private volatile bool _menuCreated;
    private int _menuWidth;   // window size stored at ShowMenu — drives hit-testing
    private int _menuHeight;

    // Drag state (worker thread only, set/read in the menu WndProc).
    // Trạng thái kéo (chỉ worker thread, đặt/đọc trong WndProc của menu).
    private bool _pressActive;
    private bool _dragging;
    private int _pressScreenX;
    private int _pressScreenY;
    private int _grabOffsetX; // press point minus window top-left at WM_LBUTTONDOWN
    private int _grabOffsetY;

    // Menu WNDPROC routing: a Windows class is registered ONCE per process, so a class-wide
    // instance delegate would serve every menu window of the process with the FIRST host
    // instance's state (stored size, drag state, events) — wrong as soon as a second host exists
    // (real-Win32 tests). The static dispatcher looks the owning host up by hwnd instead; being
    // static, the native callback is also rooted for the process lifetime (no GC thunk hazard).
    // Định tuyến WNDPROC của menu: một class của Windows chỉ đăng ký MỘT LẦN mỗi process, nên
    // delegate instance cấp-class sẽ phục vụ mọi cửa sổ menu của process bằng state của host ĐẦU
    // TIÊN (cỡ đã lưu, state kéo, event) — sai ngay khi có host thứ hai (test real-Win32). Dispatcher
    // tĩnh tra host sở hữu theo hwnd; vì tĩnh, callback native cũng được giữ suốt process (không
    // còn rủi ro thunk bị GC thu hồi).
    private static readonly ConcurrentDictionary<nint, Win32OverlayHost> MenuHostsByHwnd = new();
    private static readonly NativeMethods.WNDPROC MenuWndProcDispatcher = DispatchMenuWndProc;

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

        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() =>
        {
            try
            {
                CreateWindowOnWorker();
            }
            finally
            {
                SignalDone(done);
            }
        });

        EnsureWorkerStarted(done);
    }

    /// <summary>
    /// Moves/resizes the window to the given physical-pixel rectangle, keeps it topmost and
    /// shows it without activating it. The box inserts after the menu window when one exists,
    /// so ReTran's own menu always sits above the boxes (both windows are topmost).
    /// Di chuyển/đổi cỡ cửa sổ về hình chữ nhật pixel vật lý cho trước, giữ trên cùng và hiện mà
    /// không kích hoạt nó. Box chèn sau cửa sổ menu khi đã có menu, để menu của chính ReTran
    /// luôn nằm phía trên các box (cả hai window đều topmost).
    /// </summary>
    /// <param name="x">Left edge in physical pixels. Mép trái theo pixel vật lý.</param>
    /// <param name="y">Top edge in physical pixels. Mép trên theo pixel vật lý.</param>
    /// <param name="width">Width in physical pixels. Chiều rộng theo pixel vật lý.</param>
    /// <param name="height">Height in physical pixels. Chiều cao theo pixel vật lý.</param>
    public void Show(int x, int y, int width, int height) => Enqueue(() =>
    {
        if (_hwnd == 0) return;
        NativeMethods.SetWindowPos(_hwnd, BoxInsertAfter(_menuHwnd), x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    });

    /// <summary>
    /// Z-order anchor for the box window: the menu hwnd when the menu window exists, otherwise
    /// HWND_TOPMOST. Both windows are topmost, so inserting after the menu keeps the boxes below
    /// ReTran's own menu without affecting other topmost windows.
    /// Neo z-order cho cửa sổ box: hwnd menu khi đã có cửa sổ menu, không thì HWND_TOPMOST. Cả hai
    /// window đều topmost nên chèn sau menu giữ các box nằm dưới menu của chính ReTran mà không
    /// ảnh hưởng các window topmost khác.
    /// </summary>
    /// <param name="menuHwnd">The menu window handle, or 0 when not created yet. Handle cửa sổ menu, hoặc 0 khi chưa tạo.</param>
    /// <returns>The insert-after handle for SetWindowPos. Handle chèn-sau cho SetWindowPos.</returns>
    internal static nint BoxInsertAfter(nint menuHwnd) =>
        menuHwnd != 0 ? menuHwnd : NativeMethods.HWND_TOPMOST;

    /// <summary>
    /// Whether the menu window has been created on the worker thread. Distinct from
    /// <see cref="IsCreated"/>, which refers to the box window.
    /// Cửa sổ menu đã được tạo trên worker thread hay chưa. Khác <see cref="IsCreated"/> — cái đó
    /// nói về cửa sổ box.
    /// </summary>
    public bool MenuCreated => _menuCreated;

    /// <summary>
    /// The menu window handle, 0 before it is created (consumed by <see cref="BoxInsertAfter"/>
    /// for the box z-order). Handle cửa sổ menu, 0 khi chưa tạo (dùng cho BoxInsertAfter để xếp
    /// z-order của box).
    /// </summary>
    public nint MenuHwnd => _menuHwnd;

    /// <summary>
    /// Creates the menu window on the worker thread (idempotent, waits up to 5 s); starts the
    /// worker thread first if the box window was never created.
    /// Tạo cửa sổ menu trên worker thread (idempotent, chờ tối đa 5 giây); khởi worker thread
    /// trước nếu chưa bao giờ tạo cửa sổ box.
    /// </summary>
    public void CreateMenu()
    {
        if (_disposed) return;

        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() =>
        {
            try
            {
                CreateMenuWindowOnWorker();
            }
            finally
            {
                SignalDone(done);
            }
        });

        EnsureWorkerStarted(done);
    }

    /// <summary>
    /// Moves/resizes the menu window to the given physical-pixel rectangle and shows it without
    /// activating it. The size is stored for WM_NCHITTEST (panel = window inset by the shadow
    /// margin). Di chuyển/đổi cỡ cửa sổ menu về hình chữ nhật pixel vật lý cho trước và hiện mà
    /// không kích hoạt nó. Kích thước được lưu cho WM_NCHITTEST (panel = window trừ viền shadow).
    /// </summary>
    /// <param name="x">Left edge in physical pixels. Mép trái theo pixel vật lý.</param>
    /// <param name="y">Top edge in physical pixels. Mép trên theo pixel vật lý.</param>
    /// <param name="width">Width in physical pixels (panel plus shadow margins). Chiều rộng theo pixel vật lý (panel cộng viền shadow).</param>
    /// <param name="height">Height in physical pixels (panel plus shadow margins). Chiều cao theo pixel vật lý (panel cộng viền shadow).</param>
    public void ShowMenu(int x, int y, int width, int height) => Enqueue(() =>
    {
        if (_menuHwnd == 0) return;
        _menuWidth = width;
        _menuHeight = height;
        NativeMethods.SetWindowPos(_menuHwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    });

    /// <summary>
    /// Hides the menu window without destroying it. Ẩn cửa sổ menu mà không hủy nó.
    /// </summary>
    public void HideMenu() => Enqueue(() =>
    {
        if (_menuHwnd != 0) NativeMethods.ShowWindow(_menuHwnd, NativeMethods.SW_HIDE);
    });

    /// <summary>
    /// Pushes the menu bitmap to the menu window with UpdateLayeredWindow. The bitmap must be
    /// 32bpp premultiplied (Format32bppPArgb) and exactly window-sized as last set by
    /// <see cref="ShowMenu"/> — a mismatch resizes the layered surface out of sync with the
    /// window rectangle. Đẩy bitmap menu vào cửa sổ bằng UpdateLayeredWindow. Bitmap phải là 32bpp
    /// premultiplied (Format32bppPArgb) và đúng cỡ window do ShowMenu đặt gần nhất — lệch kích
    /// thước sẽ làm bề mặt layered mất đồng bộ với khung cửa sổ.
    /// </summary>
    /// <param name="bitmap">The rendered menu, 32bpp premultiplied, window-sized. Menu đã render, 32bpp premultiplied, đúng cỡ window.</param>
    public void DrawMenu(SD.Bitmap bitmap) => Enqueue(() =>
    {
        if (_menuHwnd == 0) return;
        PushBitmap(bitmap, _menuHwnd, bitmap.Width, bitmap.Height);
    });

    /// <summary>
    /// Raised on the worker thread when a press inside the panel ends without dragging: the point
    /// in PANEL coordinates (origin = panel top-left, i.e. window coordinates minus the shadow
    /// margin). Fired từ worker thread khi một lần nhấn trong panel kết thúc mà không kéo: điểm ở
    /// TOÁN HỆ PANEL (gốc = góc trái trên panel, tức toạ độ window trừ viền shadow).
    /// </summary>
    public event Action<int, int>? PanelClicked;

    /// <summary>
    /// Raised on the worker thread after a drag ends: the menu window's new top-left in desktop
    /// physical pixels. Fired từ worker thread sau khi kéo xong: góc trên trái mới của cửa sổ menu
    /// theo pixel vật lý desktop.
    /// </summary>
    public event Action<int, int>? DragFinished;

    // Starts the worker thread exactly once (first of Create/CreateMenu wins) and waits for
    // <paramref name="done"/> up to 5 s when this call started it. Callers that lose the race
    // return immediately — their enqueued action runs on the already-running worker.
    // Khởi worker thread đúng một lần (gọi nào trong Create/CreateMenu đến trước thắng) và chờ
    // done tối đa 5 giây khi chính gọi đó khởi thread. Gọi thua cuộc trả về ngay — action đã
    // enqueue của nó sẽ chạy trên worker đang chạy.
    private void EnsureWorkerStarted(ManualResetEventSlim? done)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 1) return;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ReTranOverlayHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        done?.Wait(TimeSpan.FromSeconds(5));
    }

    private static void SignalDone(ManualResetEventSlim done)
    {
        try { done.Set(); }
        catch (ObjectDisposedException) { } // caller gave up waiting — ignore
    }

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
    public void Draw(IReadOnlyList<DrawBox> boxes) => Enqueue(() => DrawOnWorker(boxes));

    /// <summary>
    /// Stops the message pump, destroys both native windows (box and menu) and joins the worker
    /// thread (timeout 5 s). Idempotent. Dừng message pump, hủy cả hai native window (box và menu)
    /// và join worker thread (timeout 5 giây). Idempotent.
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
            if (_menuHwnd != 0)
            {
                NativeMethods.DestroyWindow(_menuHwnd);
                _menuHwnd = 0;
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

    // Creates the second window on the worker thread: the floating menu. Unlike the box it is NOT
    // WS_EX_TRANSPARENT — the menu must receive clicks. Its class is registered with the static
    // MenuWndProcDispatcher, which routes each window's messages to its owning host instance
    // (see MenuHostsByHwnd) — an instance delegate cannot be registered directly because a class
    // is registered once per process and later hosts' windows would keep calling the first one.
    // Tạo cửa sổ thứ hai trên worker thread: menu nổi. Khác với box, nó KHÔNG mang
    // WS_EX_TRANSPARENT — menu phải nhận click. Class của nó đăng ký với MenuWndProcDispatcher
    // tĩnh, dispatcher này định tuyến message của từng cửa sổ về đúng host sở hữu (xem
    // MenuHostsByHwnd) — không thể đăng ký thẳng delegate instance vì một class chỉ đăng ký một
    // lần mỗi process và cửa sổ của host sau sẽ mãi gọi host đầu tiên.
    private void CreateMenuWindowOnWorker()
    {
        if (_menuHwnd != 0)
        {
            _menuCreated = true;
            return;
        }

        nint hInstance = NativeMethods.GetModuleHandleW(null);
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = MenuWndProcDispatcher,
            hInstance = hInstance,
            lpszClassName = MenuWindowClassName,
        };
        NativeMethods.RegisterClassEx(ref wc); // already registered → 0, fine

        _menuHwnd = NativeMethods.CreateWindowExW(
            MenuWindowExStyles(),
            MenuWindowClassName,
            string.Empty,
            NativeMethods.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_menuHwnd != 0) MenuHostsByHwnd[_menuHwnd] = this; // bind the window to THIS host
        _menuCreated = _menuHwnd != 0;
    }

    // Routes a menu window's messages to its owning host (bound in CreateMenuWindowOnWorker).
    // Windows can deliver messages during CreateWindowExW — before the binding exists — so an
    // unbound hwnd falls back to DefWindowProc.
    // Định tuyến message của cửa sổ menu về host sở hữu (được gắn trong CreateMenuWindowOnWorker).
    // Windows có thể gửi message ngay trong CreateWindowExW — trước khi có gắn kết — nên hwnd chưa
    // gắn rơi về DefWindowProc.
    private static nint DispatchMenuWndProc(nint hWnd, uint msg, nint wParam, nint lParam) =>
        MenuHostsByHwnd.TryGetValue(hWnd, out Win32OverlayHost? host)
            ? host.MenuWndProc(hWnd, msg, wParam, lParam)
            : NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    /// <summary>
    /// Extended window styles of the menu window: tool window (no Alt+Tab entry), topmost, layered
    /// (per-pixel alpha) and non-activating — deliberately WITHOUT WS_EX_TRANSPARENT so the menu
    /// receives mouse input. Pure helper (no Win32 call) so tests can assert the bit pattern.
    /// Styles mở rộng của cửa sổ menu: tool window (không hiện trong Alt+Tab), topmost, layered
    /// (alpha theo pixel) và không kích hoạt — cố ý KHÔNG có WS_EX_TRANSPARENT để menu nhận input
    /// chuột. Helper thuần (không gọi Win32) để test kiểm tra được bit pattern.
    /// </summary>
    internal static uint MenuWindowExStyles() =>
        NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED
            | NativeMethods.WS_EX_NOACTIVATE;

    /// <summary>
    /// Whether a window-coordinate point of the menu window falls inside its rounded panel: the
    /// panel is the window inset by ShadowMargin on all sides and the four corner squares beyond
    /// the CornerRadius arc are outside. Mirrors FloatingMenuLayout's private IsInsideRoundedRect —
    /// keep the two in sync. Computed from the stored window size only (no MenuState, no layout
    /// Rows()/HitTest) so it is allocation-free on every WM_NCHITTEST.
    /// Điểm trong toạ độ window của menu có nằm trong panel bo góc không: panel là window trừ
    /// ShadowMargin mọi phía và bốn ô vuông góc ngoài cung CornerRadius là ngoài. Chép theo
    /// IsInsideRoundedRect private của FloatingMenuLayout — giữ đồng bộ hai bên. Tính chỉ từ kích
    /// thước window đã lưu (không MenuState, không Rows()/HitTest của layout) để không phân bổ gì
    /// ở mỗi WM_NCHITTEST.
    /// </summary>
    /// <param name="x">Point X in window coordinates (physical pixels). Toạ độ X điểm trong hệ window (pixel vật lý).</param>
    /// <param name="y">Point Y in window coordinates (physical pixels). Toạ độ Y điểm trong hệ window (pixel vật lý).</param>
    /// <param name="windowWidth">Window width including the shadow margin. Rộng window kể cả viền shadow.</param>
    /// <param name="windowHeight">Window height including the shadow margin. Cao window kể cả viền shadow.</param>
    /// <returns>True when the point is inside the rounded panel (HTCLIENT), false for the shadow ring and corner arcs (HTTRANSPARENT). True khi điểm trong panel bo tròn (HTCLIENT), false với vành shadow và các góc cung (HTTRANSPARENT).</returns>
    internal static bool MenuPointInsidePanel(int x, int y, int windowWidth, int windowHeight)
    {
        const int m = FloatingMenuLayout.ShadowMargin;
        int px = m, py = m;
        int pw = windowWidth - 2 * m;
        int ph = windowHeight - 2 * m;
        if (pw <= 0 || ph <= 0) return false;
        if (x < px || x >= px + pw || y < py || y >= py + ph) return false;

        // Corner squares beyond the arc are transparent — same math as the layout's hit-test.
        // Bốn ô vuông góc ngoài cung là trong suốt — cùng phép tính với hit-test của layout.
        int r = Math.Min(FloatingMenuLayout.CornerRadius, Math.Min(pw, ph) / 2);
        if (r <= 0) return true;

        int dx = -1, dy = -1;
        if (x < px + r && y < py + r) { dx = px + r - x; dy = py + r - y; }
        else if (x >= px + pw - r && y < py + r) { dx = x - (px + pw - r); dy = py + r - y; }
        else if (x < px + r && y >= py + ph - r) { dx = px + r - x; dy = y - (py + ph - r); }
        else if (x >= px + pw - r && y >= py + ph - r) { dx = x - (px + pw - r); dy = y - (py + ph - r); }

        return dx < 0 || dx * dx + dy * dy <= r * r;
    }

    private void DrawOnWorker(IReadOnlyList<DrawBox> boxes)
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
                DrawBoxes(g, boxes, width, height);
            }

            PushBitmap(bitmap, _hwnd, width, height);
        }
    }

    /// <summary>
    /// Draws the overlay boxes onto an existing graphics surface: teal corner ticks on each
    /// source box plus a dark rounded pill inside it holding the translation (up to two wrapped
    /// lines — four for merged paragraph blocks, font size derived from the box height and shrunk
    /// until it fits). Exposed internally so tests can pixel-check the real drawing path.
    /// Vẽ các box overlay lên surface đã có: tick teal 4 góc trên box gốc kèm pill bo góc tối
    /// bên trong chứa bản dịch (tối đa hai dòng wrap — bốn dòng với khối đoạn văn gộp, cỡ chữ suy
    /// ra từ chiều cao box và thu nhỏ đến khi vừa). Mở nội bộ để test kiểm tra pixel của đúng đường vẽ thật.
    /// </summary>
    /// <param name="g">Target graphics surface. Surface đồ hoạ đích.</param>
    /// <param name="boxes">Boxes in viewport pixel coordinates. Các box theo toạ độ pixel viewport.</param>
    /// <param name="viewportWidth">Viewport width in pixels. Chiều rộng viewport (pixel).</param>
    /// <param name="viewportHeight">Viewport height in pixels. Chiều cao viewport (pixel).</param>
    internal static void DrawBoxes(SD.Graphics g, IReadOnlyList<DrawBox> boxes,
        int viewportWidth, int viewportHeight)
    {
        float ui = BoxAnnotationStyle.DecorationScale(viewportWidth);
        using var tickPen = new SD.Pen(BoxAnnotationStyle.TickColor, MathF.Max(1f, 1.6f * ui))
        {
            StartCap = SD.Drawing2D.LineCap.Square,
            EndCap = SD.Drawing2D.LineCap.Square,
        };
        using var pillBrush = new SD.SolidBrush(BoxAnnotationStyle.PillFill);
        using var pillPen = new SD.Pen(BoxAnnotationStyle.PillBorder, MathF.Max(1f, ui));
        using var textBrush = new SD.SolidBrush(BoxAnnotationStyle.TextColor);
        using var format = BoxAnnotationStyle.CreateTextFormat();
        using var pillPath = new SD.Drawing2D.GraphicsPath();

        foreach (DrawBox box in boxes)
        {
            var rect = new SD.RectangleF((float)box.X, (float)box.Y, (float)box.W, (float)box.H);
            if (rect.Width < 1f || rect.Height < 1f) continue;

            BoxAnnotationStyle.DrawTicks(g, tickPen, rect, MathF.Max(3f, 7f * ui));

            string text = box.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Pill follows the mockup (4 px inset left/top/bottom, flush right).
            // Pill theo mockup (inset 4 px trái/trên/dưới, sát mép phải).
            SD.RectangleF inner = BoxAnnotationStyle.InnerRect(rect);
            float innerW = inner.Width, innerH = inner.Height;
            if (innerW < 2f || innerH < 2f) continue;

            // Khối gộp nhiều dòng (paragraph) cho phép tới 4 dòng wrap trong pill — khớp FrameAnnotator phía core.
            int maxLines = box.IsParagraph ? 4 : 2;
            BoxAnnotationStyle.TextFit fit = BoxAnnotationStyle.Fit(
                g, text, innerW, innerH, rect.Height, format, maxLines: maxLines,
                shrink: true,
                maxFontSize: BoxAnnotationStyle.MaxFontSize * ui);
            SD.SizeF block = fit.Block;
            if (!fit.Fits)
            {
                text = BoxAnnotationStyle.TruncateToFit(g, text, fit.Size, innerW, innerH, format, maxLines: maxLines);
                block = BoxAnnotationStyle.MeasureBlock(g, text, fit.Size, innerW, format);
            }

            float padX = BoxAnnotationStyle.PadX(fit.Size);
            float padY = BoxAnnotationStyle.PadY(fit.Size);
            float pillW = innerW;
            float pillH = MathF.Max(innerH, block.Height + 2f * padY); // tiny-box fallback may overflow
            float pillX = Math.Clamp(inner.X, 0f, MathF.Max(0f, viewportWidth - pillW));
            float pillY = Math.Clamp(inner.Y, 0f, MathF.Max(0f, viewportHeight - pillH));

            BoxAnnotationStyle.BuildRoundedRect(pillPath, new SD.RectangleF(pillX, pillY, pillW, pillH), 3f * ui);
            g.FillPath(pillBrush, pillPath);
            g.DrawPath(pillPen, pillPath);
            g.DrawString(text, BoxAnnotationStyle.GetFont(fit.Size), textBrush,
                new SD.RectangleF(pillX + padX, pillY + padY,
                    MathF.Max(1f, pillW - 2f * padX + 1f),
                    MathF.Max(1f, pillH - 2f * padY + 1f)),
                format);
        }
    }

    // Draws the bitmap's premultiplied bits onto the given window; pptDst is null so the position
    // previously set by SetWindowPos is kept. The bitmap must be 32bpp premultiplied (the AC_SRC_ALPHA
    // blend reads per-pixel alpha) and exactly window-sized, or the layered surface desyncs from the
    // window rectangle. Shared by the box (DrawOnWorker) and the menu (DrawMenu).
    // Vẽ các bit premultiplied của bitmap lên cửa sổ cho trước; pptDst là null để giữ vị trí
    // SetWindowPos đặt trước đó. Bitmap phải 32bpp premultiplied (blend AC_SRC_ALPHA đọc alpha theo
    // pixel) và đúng cỡ window, nếu không bề mặt layered mất đồng bộ với khung cửa sổ. Dùng chung
    // cho box (DrawOnWorker) và menu (DrawMenu).
    private void PushBitmap(SD.Bitmap bitmap, nint hwnd, int width, int height)
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
            NativeMethods.UpdateLayeredWindow(hwnd, screenDc, IntPtr.Zero, ref size, memDc,
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
    /// Menu window procedure (instance method — it owns the drag state). Handles: WM_ERASEBKGND
    /// (layered → never erase), WM_NCHITTEST (HTTRANSPARENT outside the rounded panel, HTCLIENT
    /// inside), WM_MOUSEACTIVATE (MA_NOACTIVATE — a real click must not activate the window) and
    /// the press/drag/click state machine on WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_LBUTTONUP with
    /// mouse capture held for the duration of a press. Mouse-message points arrive in CLIENT
    /// coordinates and are translated to screen coordinates before entering that state machine.
    /// Everything else goes to DefWindowProc. Deliberately never calls
    /// SetForegroundWindow/Activate — the menu must not steal focus from a game.
    /// Thủ tục cửa sổ của menu (instance method — sở hữu state kéo). Xử lý: WM_ERASEBKGND
    /// (layered → không bao giờ xóa nền), WM_NCHITTEST (HTTRANSPARENT ngoài panel bo tròn,
    /// HTCLIENT bên trong), WM_MOUSEACTIVATE (MA_NOACTIVATE — click thật không được kích hoạt cửa
    /// sổ) và máy trạng thái nhấn/kéo/click trên WM_LBUTTONDOWN / WM_MOUSEMOVE / WM_LBUTTONUP, giữ
    /// mouse capture suốt thời gian nhấn. Điểm trong message chuột đến theo toạ độ CLIENT và được
    /// dịch sang toạ độ màn hình trước khi vào máy trạng thái đó. Phần còn lại chuyển DefWindowProc.
    /// Cố ý KHÔNG gọi SetForegroundWindow/Activate — menu không được cướp focus của game.
    /// </summary>
    private nint MenuWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_ERASEBKGND:
                // Layered windows paint via UpdateLayeredWindow only.
                // Cửa sổ layered chỉ vẽ qua UpdateLayeredWindow.
                return IntPtr.Zero;

            case NativeMethods.WM_NCHITTEST:
            {
                var (sx, sy) = PointFromLParam(lParam);
                if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
                    return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

                // Outside the rounded panel (shadow ring + corner arcs) the click falls through.
                // Ngoài panel bo tròn (vành shadow + các góc cung) thì click rơi xuyên qua.
                bool inside = MenuPointInsidePanel(sx - rect.Left, sy - rect.Top, _menuWidth, _menuHeight);
                return new nint(inside ? NativeMethods.HTCLIENT : NativeMethods.HTTRANSPARENT);
            }

            case NativeMethods.WM_LBUTTONDOWN:
            {
                // A press can only arrive inside the rounded panel (WM_NCHITTEST filters the rest),
                // and its lParam carries CLIENT coordinates — see ScreenPointFromMouseLParam.
                // Nhấn chỉ có thể đến khi nằm trong panel bo tròn (WM_NCHITTEST lọc phần còn lại), và
                // lParam của nó mang toạ độ CLIENT — xem ScreenPointFromMouseLParam.
                var (sx, sy) = ScreenPointFromMouseLParam(hWnd, lParam);
                _pressActive = true;
                _dragging = false;
                _pressScreenX = sx;
                _pressScreenY = sy;

                // The grab offset is fixed at press time so the grabbed point follows the mouse.
                // Offset nắm giữ cố định lúc nhấn để điểm nắm theo con trỏ.
                if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
                {
                    _grabOffsetX = sx - rect.Left;
                    _grabOffsetY = sy - rect.Top;
                }
                else
                {
                    _grabOffsetX = 0;
                    _grabOffsetY = 0;
                }

                // Capture so moves/releases keep arriving even when the cursor leaves the window
                // mid-drag (otherwise stale press state could make the menu follow a later,
                // unpressed mouse move). SetCapture does not activate the window.
                // Giữ capture để vẫn nhận move/release khi con trỏ rời cửa sổ giữa chừng khi kéo
                // (nếu không, state nhấn cũ có thể làm menu theo một lần di chuột sau đó không
                // hề nhấn nút). SetCapture không kích hoạt cửa sổ.
                NativeMethods.SetCapture(hWnd);
                return IntPtr.Zero;
            }

            case NativeMethods.WM_MOUSEMOVE:
            {
                if (!_pressActive)
                    return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

                var (sx, sy) = ScreenPointFromMouseLParam(hWnd, lParam);
                int dx = sx - _pressScreenX;
                int dy = sy - _pressScreenY;

                // Displacement beyond the drag threshold on either axis turns the press into a drag.
                // Lệch vượt ngưỡng kéo theo bất kỳ trục nào thì nhấn chuyển thành kéo.
                if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
                    _dragging = true;

                if (_dragging)
                    NativeMethods.SetWindowPos(_menuHwnd, NativeMethods.HWND_TOPMOST,
                        sx - _grabOffsetX, sy - _grabOffsetY, 0, 0,
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOSIZE);
                return IntPtr.Zero;
            }

            case NativeMethods.WM_LBUTTONUP:
            {
                var (sx, sy) = ScreenPointFromMouseLParam(hWnd, lParam);
                bool dragged = _dragging;
                _pressActive = false; // reset before firing — handlers may re-enter the host
                _dragging = false;
                NativeMethods.ReleaseCapture(); // no-op when we hold no capture

                if (dragged)
                {
                    // Report the window's new desktop position. Báo vị trí desktop mới của window.
                    if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect))
                        DragFinished?.Invoke(rect.Left, rect.Top);
                }
                else if (NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect)
                         && MenuPointInsidePanel(sx - rect.Left, sy - rect.Top, _menuWidth, _menuHeight))
                {
                    // Panel coordinate system: origin at the panel's top-left corner.
                    // Toán hệ panel: gốc tại góc trên trái của panel.
                    const int m = FloatingMenuLayout.ShadowMargin;
                    PanelClicked?.Invoke(sx - rect.Left - m, sy - rect.Top - m);
                }
                return IntPtr.Zero;
            }

            case NativeMethods.WM_CAPTURECHANGED:
                // The system released our capture (e.g. focus was stolen mid-press) without a
                // WM_LBUTTONUP — drop the press state so it cannot leak into a later gesture.
                // Hệ thống thu hồi capture của chúng ta (ví dụ mất focus giữa chừng khi nhấn) mà
                // không có WM_LBUTTONUP — xóa state nhấn để không rò sang thao tác sau đó.
                _pressActive = false;
                _dragging = false;
                return IntPtr.Zero;

            case NativeMethods.WM_MOUSEACTIVATE:
                // MA_NOACTIVATE: deliver the click but never activate — WS_EX_NOACTIVATE alone did
                // not prevent it (the DefWindowProc default answer is MA_ACTIVATE, and a real click
                // made the system activate the menu window: WM_ACTIVATEAPP(WA_ACTIVE) +
                // WM_ACTIVATE(WA_CLICKACTIVE) + WM_SETFOCUS reached this WndProc, dragging the app
                // and the keyboard focus away from the game). Verified live 2026-11-12, T23f.
                // MA_NOACTIVATE: gửi click nhưng không bao giờ kích hoạt — chỉ WS_EX_NOACTIVATE là
                // không đủ (câu trả lời mặc định của DefWindowProc là MA_ACTIVATE, và một click thật
                // làm hệ thống kích hoạt cửa sổ menu: WM_ACTIVATEAPP(WA_ACTIVE) +
                // WM_ACTIVATE(WA_CLICKACTIVE) + WM_SETFOCUS đã đến WndProc này, kéo app và focus bàn
                // phím khỏi game). Đã đo trực tiếp 2026-11-12, T23f.
                return new nint(NativeMethods.MA_NOACTIVATE);

            case NativeMethods.WM_NCDESTROY:
                // The window is gone — drop the hwnd → host binding taken in CreateMenuWindowOnWorker.
                // Cửa sổ không còn — bỏ gắn kết hwnd → host đã lấy trong CreateMenuWindowOnWorker.
                MenuHostsByHwnd.TryRemove(hWnd, out _);
                return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // Decodes the packed point in a mouse/hit-test lParam: two SIGNED 16-BIT words with X in the
    // low word and Y in the high word — the packing Windows itself uses for WM_NCHITTEST and the
    // mouse messages (the shape MAKELPARAM produces and GET_X_LPARAM/GET_Y_LPARAM read; negative
    // coordinates sign-extend within their own 16-bit word). Reading it as two 32-bit dwords — the
    // shape a previous revision assumed — decoded every real message into garbage (Y always 0, X
    // the whole packed value), so every hit-test point fell outside the panel and every click fell
    // through the menu (fixed 2026-11-12, T23e). The two message families differ in SPACE, not in
    // packing: WM_NCHITTEST packs a SCREEN point, the mouse messages pack a CLIENT point — see
    // ScreenPointFromMouseLParam (fixed 2026-11-12, T23f).
    // Giải mã điểm đóng gói trong lParam của message chuột/hit-test: hai từ 16-BIT có dấu với X ở
    // word thấp và Y ở word cao — đúng cách Windows đóng gói cho WM_NCHITTEST và các message chuột
    // (dạng MAKELPARAM tạo ra và GET_X_LPARAM/GET_Y_LPARAM đọc; toạ độ âm sign-extend trong chính
    // word 16-bit của nó). Đọc thành hai dword 32-bit — dạng một bản sửa trước từng giả định — làm
    // mọi message thật giải mã thành rác (Y luôn 0, X là cả giá trị đóng gói), nên mọi điểm hit-test
    // rơi ngoài panel và mọi click rơi xuyên menu (sửa 2026-11-12, T23e). Hai họ message khác nhau ở
    // KHÔNG GIAN, không ở cách đóng gói: WM_NCHITTEST đóng gói điểm MÀN HÌNH, còn message chuột đóng
    // gói điểm CLIENT — xem ScreenPointFromMouseLParam (sửa 2026-11-12, T23f).
    private static (int X, int Y) PointFromLParam(nint lParam)
    {
        long lp = lParam.ToInt64();
        return ((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
    }

    /// <summary>
    /// Screen point of a MOUSE message (WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP): those pack
    /// the cursor position in CLIENT coordinates — relative to the window's client area — while the
    /// press/drag/click math works in screen coordinates, so the window origin is added back here.
    /// WM_NCHITTEST and the wheel messages are the ones that already carry screen coordinates and
    /// must NOT go through this helper. The origin is read at this moment, which is exactly the
    /// origin Windows used to pack the message.
    /// Điểm màn hình của message CHUỘT (WM_MOUSEMOVE / WM_LBUTTONDOWN / WM_LBUTTONUP): chúng đóng gói
    /// vị trí con trỏ theo toạ độ CLIENT — tương đối với vùng client của cửa sổ — trong khi phép tính
    /// nhấn/kéo/click làm việc theo toạ độ màn hình, nên gốc cửa sổ được cộng lại ở đây. WM_NCHITTEST
    /// và các message bánh xe mới là loại đã mang toạ độ màn hình — KHÔNG được đi qua helper này. Gốc
    /// được đọc ngay lúc này, đúng bằng gốc Windows đã dùng để đóng gói message.
    /// </summary>
    /// <param name="hWnd">The menu window that received the message. Cửa sổ menu nhận message.</param>
    /// <param name="lParam">The message's lParam (two signed 16-bit client coordinates). lParam của message (hai toạ độ client 16-bit có dấu).</param>
    /// <returns>The same point in desktop physical pixels, or the raw point when the rect is unavailable. Cùng điểm đó theo pixel vật lý desktop, hoặc điểm thô khi không đọc được rect.</returns>
    private static (int X, int Y) ScreenPointFromMouseLParam(nint hWnd, nint lParam)
    {
        var (cx, cy) = PointFromLParam(lParam);
        if (!NativeMethods.GetWindowRect(hWnd, out NativeMethods.RECT rect)) return (cx, cy);
        return (cx + rect.Left, cy + rect.Top);
    }

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
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal static readonly nint HWND_TOPMOST = new(-1);

        // WM_NCHITTEST hit codes (HTTRANSPARENT is -1: the click falls through to the window below).
        // Mã hit của WM_NCHITTEST (HTTRANSPARENT là -1: click rơi xuống window bên dưới).
        internal const int HTCLIENT = 1;
        internal const int HTTRANSPARENT = -1;

        // WM_MOUSEACTIVATE answer: hand the click over without activating the window.
        // Câu trả lời WM_MOUSEACTIVATE: nhận click mà không kích hoạt cửa sổ.
        internal const int MA_NOACTIVATE = 3;

        internal const uint WM_QUIT = 0x0012;
        internal const uint WM_ERASEBKGND = 0x0014;
        internal const uint WM_MOUSEACTIVATE = 0x0021;
        internal const uint WM_NCDESTROY = 0x0082;
        internal const uint WM_NCHITTEST = 0x0084;
        internal const uint WM_MOUSEMOVE = 0x0200;
        internal const uint WM_LBUTTONDOWN = 0x0201;
        internal const uint WM_LBUTTONUP = 0x0202;
        // 0x0215 — the previous value 0x0210 is WM_PARENTNOTIFY, so the capture-lost reset below
        // never ran; the live trace showed 0x0215 arriving on every release (fixed 2026-11-12, T23f).
        // 0x0215 — giá trị cũ 0x0210 là WM_PARENTNOTIFY nên nhánh xoá state khi mất capture bên dưới
        // chưa bao giờ chạy; trace trực tiếp cho thấy 0x0215 đến ở mọi lần nhả (sửa 2026-11-12, T23f).
        internal const uint WM_CAPTURECHANGED = 0x0215;
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
        internal static extern bool GetWindowRect(nint hWnd, out RECT rect);

        [DllImport("user32.dll")]
        internal static extern nint SetCapture(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReleaseCapture();

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
