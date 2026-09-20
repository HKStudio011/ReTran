namespace ReTran.Core.Capture;

/// <summary>Describes which surface to capture. Mô tả bề mặt cần chụp.</summary>
public abstract record CaptureTarget;
/// <summary>Full-screen capture of monitor by index (0 = primary). Chụp toàn màn hình theo chỉ số (0 = chính).</summary>
public sealed record ScreenTarget(int MonitorIndex) : CaptureTarget;
/// <summary>Window capture by HWND. Chụp cửa sổ theo HWND.</summary>
public sealed record WindowTarget(nint Hwnd) : CaptureTarget;

/// <summary>Event payload carrying a freshly captured frame. Dữ liệu sự kiện mang khung hình mới chụp.</summary>
public sealed class FrameArrivedEventArgs(Frame frame) : EventArgs
{
    /// <summary>The new frame (ownership transfers to the handler). Khung hình mới (handler nhận quyền sở hữu).</summary>
    public Frame Frame { get; } = frame;
}

/// <summary>
/// A frame source (screen, window, later the M5 fullscreen-hook) pushing frames via event.
/// Nguồn khung hình (màn hình, cửa sổ, sau này là hook M5) đẩy frame qua sự kiện.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>What this source captures. Nguồn này chụp cái gì.</summary>
    CaptureTarget Target { get; }
    /// <summary>Raised on each captured frame. Kích hoạt mỗi khi có khung hình.</summary>
    event EventHandler<FrameArrivedEventArgs>? FrameArrived;
    /// <summary>Starts capture. Bắt đầu chụp.</summary>
    void Start();
    /// <summary>Stops capture. Dừng chụp.</summary>
    void Stop();
    /// <summary>Whether capture is running. Có đang chụp hay không.</summary>
    bool IsRunning { get; }
}
