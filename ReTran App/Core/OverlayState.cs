namespace ReTran_App.Core;

/// <summary>
/// A single OCR text box with 0–1 relative coordinates and its translation.
/// Một hộp văn bản OCR với tọa độ tương đối 0–1 và bản dịch của nó.
/// </summary>
/// <param name="X">Left edge as a fraction of frame width. / Cạnh trái theo tỉ lệ chiều rộng khung hình.</param>
/// <param name="Y">Top edge as a fraction of frame height. / Cạnh trên theo tỉ lệ chiều cao khung hình.</param>
/// <param name="W">Width as a fraction of frame width. / Chiều rộng theo tỉ lệ chiều rộng khung hình.</param>
/// <param name="H">Height as a fraction of frame height. / Chiều cao theo tỉ lệ chiều cao khung hình.</param>
/// <param name="Text">Recognized source text. / Văn bản gốc được nhận dạng.</param>
/// <param name="Translated">Translated text for display. / Văn bản dịch để hiển thị.</param>
public sealed record OverlayBox(double X, double Y, double W, double H, string Text, string Translated);

/// <summary>
/// One annotated frame: dimensions, capture timestamp and its text boxes.
/// Một khung hình đã chú thích: kích thước, thời điểm chụp và các hộp văn bản.
/// </summary>
/// <param name="FrameWidth">Frame width in pixels. / Chiều rộng khung hình theo pixel.</param>
/// <param name="FrameHeight">Frame height in pixels. / Chiều cao khung hình theo pixel.</param>
/// <param name="TimestampUtc">Capture time in UTC. / Thời điểm chụp theo giờ UTC.</param>
/// <param name="Boxes">Text boxes spotted on this frame. / Các hộp văn bản phát hiện trên khung hình này.</param>
public sealed record OverlayFrame(int FrameWidth, int FrameHeight, DateTime TimestampUtc, IReadOnlyList<OverlayBox> Boxes);

/// <summary>
/// Single source of truth every UI surface reads for the latest overlay frame.
/// Nguồn sự thật duy nhất mọi bề mặt UI đọc để lấy khung overlay mới nhất.
/// </summary>
public sealed class OverlayState
{
    private readonly object _gate = new();
    private OverlayFrame? _latest;

    /// <summary>
    /// The latest published frame, or <see langword="null"/> when nothing was published yet.
    /// Khung hình mới nhất đã xuất bản, hoặc <see langword="null"/> khi chưa có gì được xuất bản.
    /// </summary>
    public OverlayFrame? Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>
    /// Raised after each published frame replaces the latest one.
    /// Được kích hoạt sau mỗi khung hình xuất bản thay thế khung mới nhất.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Replaces the latest frame under a lock (drops the frame if the lock is busy) then invokes <see cref="Changed"/>.
    /// Thay thế khung mới nhất dưới khóa (bỏ khung nếu khóa đang bận) rồi kích hoạt <see cref="Changed"/>.
    /// </summary>
    /// <param name="frame">The frame to publish. / Khung hình cần xuất bản.</param>
    public void Publish(OverlayFrame frame)
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
        Changed?.Invoke();
    }
}
