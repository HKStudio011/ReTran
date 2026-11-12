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
/// <param name="IsParagraph">True when the box is a merged multi-line block; false for a single line. Đúng khi hộp là khối nhiều dòng đã gộp; sai với một dòng đơn.</param>
public sealed record OverlayBox(double X, double Y, double W, double H, string Text, string Translated, bool IsParagraph = false);

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
    /// The latest published frame, or <see langword="null"/> when nothing was published yet or the
    /// feed was cleared by <see cref="Clear"/>. <see langword="null"/> means "empty snapshot".
    /// Khung hình mới nhất đã xuất bản, hoặc <see langword="null"/> khi chưa có gì được xuất bản hay
    /// feed vừa bị <see cref="Clear"/> xóa. <see langword="null"/> nghĩa là "snapshot rỗng".
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

    /// <summary>
    /// Drops the latest frame so the overlay feed reads as empty, then invokes <see cref="Changed"/>
    /// exactly once — and does nothing at all (no event) when nothing was stored. Unlike
    /// <see cref="Publish"/> this blocks on the lock instead of giving up: <c>Clear</c> is not a
    /// hot per-frame path, and <c>Publish</c> never invokes handlers while holding the lock, so a
    /// blocking wait can never be pinned by a frame publisher. <see cref="Latest"/> becoming
    /// <see langword="null"/> is the contract a late subscriber (e.g. an OBS Browser Source that
    /// connects after the pipeline stopped) relies on to get an empty snapshot instead of a finished
    /// session's text.
    /// Bỏ khung mới nhất để feed overlay đọc như rỗng, rồi kích hoạt <see cref="Changed"/> đúng một
    /// lần — và hoàn toàn không làm gì (không nổ event) khi chưa có gì được lưu. Khác
    /// <see cref="Publish"/>, method này chặn trên khóa thay vì bỏ qua: <c>Clear</c> không phải
    /// đường nóng cho từng khung hình, và <c>Publish</c> không gọi handler khi đang giữ khóa nên
    /// việc chờ blocking không thể bị một bên publish khung giữ mãi. <see cref="Latest"/> thành
    /// <see langword="null"/> là hợp đồng mà một subscriber kết nối muộn (ví dụ OBS Browser Source
    /// kết nối sau khi pipeline đã dừng) dựa vào để nhận snapshot rỗng thay vì chữ của phiên cũ.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_latest is null) return;
            _latest = null;
        }
        Changed?.Invoke();
    }
}
