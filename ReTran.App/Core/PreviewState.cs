namespace ReTran_App.Core;

/// <summary>
/// One annotated frame ready for the preview pane (data-URL JPEG straight from
/// <c>frame.annotated</c>). Một khung đã ghi chú sẵn cho ô preview (JPEG data-URL nguyên từ
/// <c>frame.annotated</c>).
/// </summary>
/// <param name="Seq">Monotonic frame number from Core, restarts at 1 per session. Số khung tăng dần từ Core, bắt đầu lại từ 1 mỗi phiên.</param>
/// <param name="W">Frame width in pixels. / Chiều rộng khung hình theo pixel.</param>
/// <param name="H">Frame height in pixels. / Chiều cao khung hình theo pixel.</param>
/// <param name="JpegBase64">Base64-encoded JPEG payload. Payload JPEG mã hoá base64.</param>
public sealed record PreviewFrame(long Seq, int W, int H, string JpegBase64);

/// <summary>
/// Single source of truth for the preview pane: latest annotated frame plus a change event the
/// page throttles before rendering. Nguồn sự thật duy nhất cho ô preview: khung ghi chú mới nhất
/// cùng event thay đổi mà trang throttle trước khi render.
/// </summary>
public sealed class PreviewState
{
    private readonly object _gate = new();
    private PreviewFrame? _latest;

    /// <summary>
    /// The latest published frame, or <see langword="null"/> when nothing was published yet.
    /// Khung hình mới nhất đã xuất bản, hoặc <see langword="null"/> khi chưa có gì được xuất bản.
    /// </summary>
    public PreviewFrame? Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>
    /// Raised after each published frame replaces the latest one.
    /// Được kích hoạt sau mỗi khung hình xuất bản thay thế khung mới nhất.
    /// </summary>
    public event Action<PreviewFrame>? Changed;

    /// <summary>
    /// Replaces the latest frame under a lock (drops the frame if the lock is busy) then invokes
    /// <see cref="Changed"/> with it. Thay thế khung mới nhất dưới khóa (bỏ khung nếu khóa đang
    /// bận) rồi kích hoạt <see cref="Changed"/> kèm khung đó.
    /// </summary>
    /// <param name="frame">The frame to publish. / Khung hình cần xuất bản.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="frame"/> is null. Ném khi <paramref name="frame"/> là null.</exception>
    public void Publish(PreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Monitor.TryEnter(_gate)) return;
        try
        {
            _latest = frame;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        Changed?.Invoke(frame);
    }
}
