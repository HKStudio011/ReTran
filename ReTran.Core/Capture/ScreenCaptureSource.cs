namespace ReTran.Core.Capture;

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// Describes an attached display monitor for full-screen capture.
/// Mô tả một màn hình hiển thị đang gắn cho chế độ chụp toàn màn hình.
/// </summary>
public sealed record MonitorInfo(
    /// <summary>Position in the sorted list; 0 is always the primary monitor. Vị trí trong danh sách đã sắp xếp; 0 luôn là màn hình chính.</summary>
    int Index,
    /// <summary>Win32 device name (e.g. \\.\DISPLAY1). Tên thiết bị Win32 (ví dụ \\.\DISPLAY1).</summary>
    string DeviceName,
    /// <summary>Width in pixels from DesktopCoordinates. Chiều rộng (pixel) từ DesktopCoordinates.</summary>
    int Width,
    /// <summary>Height in pixels from DesktopCoordinates. Chiều cao (pixel) từ DesktopCoordinates.</summary>
    int Height,
    /// <summary>True when this is the primary monitor. True nếu là màn hình chính.</summary>
    bool IsPrimary);

/// <summary>
/// Full-screen capture source backed by DXGI Desktop Duplication on a dedicated MTA thread.
/// Nguồn chụp toàn màn hình dùng DXGI Desktop Duplication trên một luồng nền MTA riêng.
/// </summary>
public class ScreenCaptureSource : ICaptureSource
{
    /// <summary>AcquireNextFrame wait budget in ms (300–500 required; never 0 / busy-loop). Thời gian chờ AcquireNextFrame (ms) — bắt buộc 300–500, không được 0.</summary>
    private const uint AcquireTimeoutMs = 400;

    /// <summary>Initial lazy re-acquire backoff in ms. Backoff ban đầu khi tái tạo pipeline (ms).</summary>
    private const int InitialBackoffMs = 250;

    /// <summary>Maximum re-acquire backoff in ms. Backoff tối đa (ms).</summary>
    private const int MaxBackoffMs = 5000;

    /// <summary>DXGI_ERROR_ACCESS_LOST — display mode change / lock screen; normal, re-acquire lazily. Thay đổi độ phân giải / khóa màn hình; bình thường, tái tạo pipeline.</summary>
    private const int AccessLostHResult = unchecked((int)0x887A0026);

    /// <summary>WAIT_TIMEOUT — no new frame yet; normal on a static desktop, not an error. Chưa có frame mới; bình thường với màn hình tĩnh, không phải lỗi.</summary>
    private const int WaitTimeoutHResult = unchecked((int)0x887A0027);

    private readonly ScreenTarget _target;
    private readonly ILogger? _logger;
    private readonly int _fps;
    private readonly string _targetDeviceName;

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile bool _running;
    private bool _disposed;

    // Pipeline state — owned exclusively by the capture thread (created and disposed there).
    // Trạng thái pipeline — chỉ luồng chụp sở hữu (tạo và dispose trên chính luồng đó).
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;
    private int _backoffMs = InitialBackoffMs;
    private int _failureCount;
    private int _formatSkipCount;

    /// <summary>
    /// Creates a screen capture source for the monitor at <paramref name="target"/>.MonitorIndex (0 = primary).
    /// Tạo nguồn chụp màn hình theo chỉ số monitor trong <paramref name="target"/> (0 = chính).
    /// </summary>
    /// <param name="target">The monitor to capture. Monitor cần chụp.</param>
    /// <param name="logger">Optional logger; all diagnostics go through it (never stdout — core stdout is JSON-RPC). Logger tùy chọn; mọi chẩn đoán qua đây (không bao giờ stdout — stdout của core là JSON-RPC).</param>
    /// <param name="fps">Target capture cadence in frames per second (>= 1). Nhịp chụp mục tiêu (frame/giây, >= 1).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when fps &lt; 1 or the monitor index is out of range.</exception>
    public ScreenCaptureSource(ScreenTarget target, ILogger? logger, int fps = 10)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _logger = logger;
        if (fps < 1) throw new ArgumentOutOfRangeException(nameof(fps), "fps must be >= 1.");
        _fps = fps;

        var monitors = EnumerateMonitors();
        if (_target.MonitorIndex < 0 || _target.MonitorIndex >= monitors.Count)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"Monitor index {_target.MonitorIndex} out of range (0..{monitors.Count - 1}).");
        _targetDeviceName = monitors[_target.MonitorIndex].DeviceName;
    }

    /// <summary>What this source captures. Nguồn này chụp cái gì.</summary>
    public CaptureTarget Target => _target;

    /// <summary>Raised on the capture thread for each captured frame (ownership of the frame transfers to the handler). Kích hoạt trên luồng chụp cho mỗi frame (handler nhận quyền sở hữu frame).</summary>
    public event EventHandler<FrameArrivedEventArgs>? FrameArrived;

    /// <summary>Whether capture is currently running. Có đang chạy chụp hay không.</summary>
    public bool IsRunning => _running;

    /// <summary>
    /// Starts the dedicated background capture thread (MTA) and begins capturing at the configured fps.
    /// Khởi động luồng nền chụp riêng (MTA) và bắt đầu chụp theo fps đã cấu hình.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the source has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown when already running.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("Capture is already running.");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _running = true;
        _thread = new Thread(() => CaptureLoop(token))
        {
            IsBackground = true,
            Name = "ReTran.ScreenCapture"
        };
        // DXGI/D3D11 must run on an MTA thread — never the JSON-RPC loop thread.
        // DXGI/D3D11 phải chạy trên luồng MTA — không dùng luồng JSON-RPC.
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>
    /// Stops capture: cancels the loop, waits for the thread to exit, and releases all DXGI/D3D11 resources.
    /// Dừng chụp: hủy vòng lặp, chờ luồng thoát, giải phóng toàn bộ tài nguyên DXGI/D3D11.
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
    /// Enumerates all attached display outputs across every adapter, sorted so the primary monitor is index 0.
    /// Liệt kê mọi output hiển thị đang gắn trên tất cả adapter, sắp xếp sao cho màn hình chính đứng ở index 0.
    /// </summary>
    /// <returns>All attached monitors; index 0 is the primary (Win32 MONITORINFOF_PRIMARY match, DesktopCoordinates origin as fallback). Mọi monitor đang gắn; index 0 là màn hình chính (khớp MONITORINFOF_PRIMARY của Win32, dự phòng theo tọa độ gốc DesktopCoordinates).</returns>
    /// <exception cref="InvalidOperationException">Thrown when DXGI reports no attached outputs.</exception>
    public static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        List<(string DeviceName, int Left, int Top, int Width, int Height)> outputs;
        using (factory)
        {
            outputs = new List<(string, int, int, int, int)>();
            for (uint a = 0; ; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out IDXGIAdapter1 adapter);
                if (!adapterResult.Success) break;
                using (adapter)
                {
                    for (uint o = 0; ; o++)
                    {
                        var outputResult = adapter.EnumOutputs(o, out IDXGIOutput output);
                        if (!outputResult.Success) break;
                        using (output)
                        {
                            var d = output.Description;
                            if (!d.AttachedToDesktop) continue;
                            var rc = d.DesktopCoordinates;
                            outputs.Add((d.DeviceName, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top));
                        }
                    }
                }
            }
        }

        if (outputs.Count == 0)
            throw new InvalidOperationException("DXGI reports no attached display outputs.");

        string? primaryName = null;
        try { primaryName = NativeMethods.GetPrimaryDeviceName(); }
        catch (DllNotFoundException) { /* non-Windows / headless: fall back to origin check */ }
        catch (EntryPointNotFoundException) { /* same as above */ }

        // Primary first: match by Win32 device name; fallback — DesktopCoordinates origin == (0,0).
        // Màn hình chính đứng đầu: khớp theo tên thiết bị Win32; dự phòng — tọa độ gốc == (0,0).
        int primaryIdx = -1;
        for (int i = 0; i < outputs.Count; i++)
        {
            var e = outputs[i];
            bool isPrimary = primaryName is not null
                ? string.Equals(e.DeviceName, primaryName, StringComparison.OrdinalIgnoreCase)
                : e.Left == 0 && e.Top == 0;
            if (isPrimary) { primaryIdx = i; break; }
        }

        var order = new List<int>(outputs.Count);
        if (primaryIdx >= 0) order.Add(primaryIdx);
        for (int i = 0; i < outputs.Count; i++)
            if (i != primaryIdx) order.Add(i);

        var result = new List<MonitorInfo>(outputs.Count);
        for (int pos = 0; pos < order.Count; pos++)
        {
            var e = outputs[order[pos]];
            result.Add(new MonitorInfo(pos, e.DeviceName, e.Width, e.Height, pos == 0 && primaryIdx >= 0));
        }
        return result;
    }

    /// <summary>
    /// Capture thread body: on a 1000/fps cadence — ensure pipeline → AcquireNextFrame → staging copy → Frame.
    /// Thân luồng chụp: theo nhịp 1000/fps ms — đảm bảo pipeline → AcquireNextFrame → copy staging → Frame.
    /// </summary>
    private void CaptureLoop(CancellationToken token)
    {
        int periodMs = Math.Max(1, 1000 / _fps);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                AttemptResult attempt;
                try
                {
                    attempt = CaptureOnce();
                }
                catch (Exception ex)
                {
                    // Never let the thread die silently — log, reset pipeline, back off.
                    // Không bao giờ để luồng chết im lặng — log, reset pipeline, lùi nhịp.
                    LogError("Unexpected capture error: {Message}", ex.Message);
                    DisposePipeline();
                    attempt = AttemptResult.Failed;
                }

                if (attempt == AttemptResult.Frame)
                {
                    _backoffMs = InitialBackoffMs;
                    _failureCount = 0;
                }
                else if (attempt == AttemptResult.Failed)
                {
                    _failureCount++;
                    InterruptibleSleep(_backoffMs, token);
                    _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
                }

                var elapsed = (int)sw.ElapsedMilliseconds;
                if (!token.IsCancellationRequested && elapsed < periodMs)
                    InterruptibleSleep(periodMs - elapsed, token);
            }
        }
        finally
        {
            DisposePipeline();
            _running = false;
        }
    }

    private enum AttemptResult { Frame, Timeout, Failed }

    /// <summary>
    /// One capture attempt: lazily (re)acquires the pipeline, waits for a frame, copies it to BGRA.
    /// Một lần thử chụp: tái tạo pipeline theo kiểu lazy, chờ frame, copy sang BGRA.
    /// </summary>
    private AttemptResult CaptureOnce()
    {
        if (_duplication is null || _device is null || _context is null)
            return EnsurePipeline() ? AttemptResult.Timeout : AttemptResult.Failed;

        var dup = _duplication!;
        var device = _device!;
        var context = _context!;

        var acquire = dup.AcquireNextFrame(AcquireTimeoutMs, out OutduplFrameInfo info, out IDXGIResource desktopRes);
        if (!acquire.Success)
        {
            int code = acquire.Code;
            if (code == WaitTimeoutHResult)
                return AttemptResult.Timeout; // static desktop: normal, no log spam. Màn hình tĩnh: bình thường, không log dồn dập.
            if (code == AccessLostHResult)
            {
                LogWarning("DXGI access lost (mode change / lock screen) — re-acquiring with backoff.");
                DisposePipeline();
                return AttemptResult.Failed;
            }
            LogError("AcquireNextFrame failed: HRESULT 0x{Code:X8} — resetting pipeline.", code);
            DisposePipeline();
            return AttemptResult.Failed;
        }

        try
        {
            // Mode info comes from the duplication's description — OutduplFrameInfo has no mode field in Vortice 3.8.3.
            // Thông tin mode lấy từ mô tả của duplication — OutduplFrameInfo không có trường mode trong Vortice 3.8.3.
            var mode = dup.Description.ModeDescription;
            if (mode.Format != Format.B8G8R8A8_UNorm)
            {
                // HDR / 10-bit output: skip the frame, never silently convert.
                // Output HDR / 10-bit: bỏ qua frame, không bao giờ chuyển đổi im lặng.
                _formatSkipCount++;
                if (_formatSkipCount == 1 || _formatSkipCount % 60 == 0)
                    LogError("Desktop output format {Format} is not B8G8R8A8_UNORM (HDR/10-bit) — frame skipped, no conversion.", mode.Format);
                return AttemptResult.Frame; // pipeline is healthy; do not back off. Pipeline bình thường; không lùi nhịp.
            }

            int width = (int)mode.Width;
            int height = (int)mode.Height;
            EnsureStaging(device, width, height);
            var staging = (ID3D11Resource)_staging!; // non-null after EnsureStaging. Khác null sau EnsureStaging.

            var desktopTexture = desktopRes.QueryInterface<ID3D11Resource>();
            try { context.CopyResource(staging, desktopTexture); }
            finally { desktopTexture.Dispose(); }

            var mapResult = context.Map(staging, 0, MapMode.Read, default, out MappedSubresource msr);
            if (!mapResult.Success)
            {
                LogError("Map of staging texture failed: HRESULT 0x{Code:X8}.", mapResult.Code);
                return AttemptResult.Failed;
            }

            try
            {
                // Row-by-row copy honoring RowPitch (it may exceed width*4).
                // Copy từng hàng theo RowPitch (có thể lớn hơn width*4).
                var pixels = new byte[width * height * 4];
                int rowBytes = width * 4;
                for (int y = 0; y < height; y++)
                {
                    // IntPtr.Add takes an int offset; RowPitch*y stays far below 2 GB for any real display size.
                    // IntPtr.Add nhận offset kiểu int; RowPitch*y luôn nhỏ hơn nhiều so với 2 GB ở mọi kích thước màn hình thực tế.
                    int rowOffset = (int)((long)y * msr.RowPitch);
                    Marshal.Copy(IntPtr.Add(msr.DataPointer, rowOffset), pixels, y * rowBytes, rowBytes);
                }

                var frame = Frame.FromBgra(width, height, pixels);
                FrameArrived?.Invoke(this, new FrameArrivedEventArgs(frame));
                return AttemptResult.Frame;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
        finally
        {
            // ReleaseFrame immediately after the copy — required by DXGI.
            // ReleaseFrame ngay sau khi copy xong — yêu cầu của DXGI.
            dup.ReleaseFrame();
            desktopRes.Dispose();
        }
    }

    /// <summary>
    /// Lazily creates factory → device (BgraSupport) → OutputDuplication for the target monitor.
    /// Tạo theo kiểu lazy: factory → device (BgraSupport) → OutputDuplication cho monitor đích.
    /// </summary>
    private bool EnsurePipeline()
    {
        try
        {
            var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            using (factory)
            {
                for (uint a = 0; ; a++)
                {
                    var adapterResult = factory.EnumAdapters1(a, out IDXGIAdapter1 adapter);
                    if (!adapterResult.Success) break;
                    using (adapter)
                    {
                        for (uint o = 0; ; o++)
                        {
                            var outputResult = adapter.EnumOutputs(o, out IDXGIOutput output);
                            if (!outputResult.Success) break;
                            using (output)
                            {
                                var d = output.Description;
                                if (!d.AttachedToDesktop ||
                                    !string.Equals(d.DeviceName, _targetDeviceName, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var output1 = output.QueryInterface<IDXGIOutput1>();
                                using (output1)
                                {
                                    CreateDeviceAndDuplicate(adapter, output1);
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SharpGenException or DllNotFoundException or InvalidOperationException)
        {
            LogError("Failed to create capture pipeline: {Message}", ex.Message);
            DisposePipeline();
            return false;
        }

        LogError("Target monitor '{Device}' not found among attached outputs.", _targetDeviceName);
        return false;
    }

    /// <summary>
    /// Creates the D3D11 device on the target adapter (DriverType must be Unknown
    /// when a non-NULL adapter is passed, per D3D11CreateDevice contract) and the OutputDuplication on it.
    /// Tạo device D3D11 trên adapter đích (DriverType phải là Unknown khi truyền adapter
    /// khác NULL, theo contract D3D11CreateDevice) và OutputDuplication trên đó.
    /// </summary>
    private void CreateDeviceAndDuplicate(IDXGIAdapter1 adapter, IDXGIOutput1 output)
    {
        var result = D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext context);
        if (!result.Success)
            throw new InvalidOperationException($"D3D11CreateDevice failed: HRESULT 0x{result.Code:X8}.");

        _device = device;
        _context = context;
        try
        {
            // DuplicateOutput throws SharpGenException on failure (e.g. ACCESS_LOST) — caller catches it.
            // DuplicateOutput ném SharpGenException khi thất bại (vd ACCESS_LOST) — người gọi bắt lỗi.
            _duplication = output.DuplicateOutput(device);
        }
        catch
        {
            device.Dispose();
            context.Dispose();
            _device = null;
            _context = null;
            throw;
        }
    }

    /// <summary>
    /// Creates (or reuses) the staging texture for the current desktop size — once per size, not per frame.
    /// Tạo (hoặc tái dùng) texture staging theo kích thước desktop hiện tại — một lần mỗi kích thước, không tạo mỗi frame.
    /// </summary>
    private void EnsureStaging(ID3D11Device device, int width, int height)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height) return;
        _staging?.Dispose();

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = SampleDescription.Default,
            Usage = ResourceUsage.Staging,
            BindFlags = 0,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = 0
        };
        _staging = device.CreateTexture2D(desc);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>Disposes duplication, staging texture, context and device (capture thread only). Dispose duplication, texture staging, context và device (chỉ luồng chụp gọi).</summary>
    private void DisposePipeline()
    {
        _duplication?.Dispose();
        _duplication = null;
        _staging?.Dispose();
        _staging = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
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

    /// <summary>Win32 helpers for primary-monitor detection (MONITORINFOF_PRIMARY + device name). Helper Win32 để nhận diện màn hình chính.</summary>
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        internal struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        /// <summary>Device name of the primary monitor via Win32, or null when unavailable. Tên thiết bị màn hình chính qua Win32, hoặc null nếu không có.</summary>
        internal static string? GetPrimaryDeviceName()
        {
            string? primary = null;

            // A named method is required — C# lambdas cannot declare the delegate's ref parameter.
            // Bắt buộc dùng method riêng — lambda C# không khai báo được tham số ref của delegate.
            bool EnumMonitorCallback(IntPtr hMonitor, IntPtr _, ref RECT __, IntPtr ___)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref mi) && (mi.dwFlags & 1u) != 0) // MONITORINFOF_PRIMARY
                    primary = mi.szDevice;
                return true;
            }

            MonitorEnumProc proc = EnumMonitorCallback;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(proc); // keep the delegate (and its closure) alive for the P/Invoke call. Giữ delegate sống trong suốt cuộc gọi P/Invoke.
            }
            return primary;
        }
    }
}
