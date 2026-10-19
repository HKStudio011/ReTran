namespace ReTran.Core.Serve;

using ReTran.Core.Capture;

/// <summary>
/// One text region to draw on the annotated frame, in coordinates normalized to 0–1 of the
/// captured frame. Một vùng text cần vẽ lên khung đã ghi chú, toạ độ chuẩn hoá 0–1 theo khung chụp.
/// </summary>
/// <param name="X">Left edge (0–1). Cạnh trái (0–1).</param>
/// <param name="Y">Top edge (0–1). Cạnh trên (0–1).</param>
/// <param name="W">Width (0–1). Chiều rộng (0–1).</param>
/// <param name="H">Height (0–1). Chiều cao (0–1).</param>
/// <param name="Text">OCR text under the box. Text OCR dưới box.</param>
/// <param name="Translated">Translation, or <see langword="null"/> while none exists. Bản dịch, hoặc <see langword="null"/> khi chưa có.</param>
public sealed record AnnotBox(double X, double Y, double W, double H, string Text, string? Translated);

/// <summary>
/// Immutable set of text regions for one captured frame, with the frame size the coordinates
/// refer to. Tập bất biến các vùng text cho một khung chụp, kèm kích thước khung mà toạ độ tham chiếu.
/// </summary>
/// <param name="FrameWidth">Captured frame width in pixels. Chiều rộng khung chụp (pixel).</param>
/// <param name="FrameHeight">Captured frame height in pixels. Chiều cao khung chụp (pixel).</param>
/// <param name="Boxes">All visible text regions (may be empty). Toàn bộ vùng text đang thấy (có thể rỗng).</param>
public sealed record AnnotSnapshot(int FrameWidth, int FrameHeight, IReadOnlyList<AnnotBox> Boxes);

/// <summary>
/// Thread-safe hand-off slot between the translation cycle (writer) and the annotation loop
/// (reader): each publish replaces the previous snapshot. Khe giao giữa luồng dịch (người ghi)
/// và vòng ghi chú khung (người đọc): mỗi lần publish ghi đè snapshot trước đó.
/// </summary>
public sealed class AnnotationSnapshot
{
    private readonly object _sync = new();
    private AnnotSnapshot? _latest;

    /// <summary>
    /// Publishes the current box list, replacing any previous snapshot. Đăng bố danh sách box
    /// hiện tại, thay snapshot trước đó.
    /// </summary>
    /// <param name="frameWidth">Captured frame width in pixels. Chiều rộng khung chụp (pixel).</param>
    /// <param name="frameHeight">Captured frame height in pixels. Chiều cao khung chụp (pixel).</param>
    /// <param name="boxes">Text regions (copied, so callers may reuse their list). Vùng text (được sao chép, người gọi dùng lại list cũng không sao).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="boxes"/> is null. Ném khi <paramref name="boxes"/> là null.</exception>
    public void Publish(int frameWidth, int frameHeight, IReadOnlyList<AnnotBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var snapshot = new AnnotSnapshot(frameWidth, frameHeight, boxes.ToArray());
        lock (_sync) _latest = snapshot;
    }

    /// <summary>
    /// Returns the latest published snapshot, or <see langword="null"/> before the first publish.
    /// Trả snapshot mới nhất đăng bố, hoặc <see langword="null"/> trước lần đầu tiên.
    /// </summary>
    /// <returns>The latest snapshot, or <see langword="null"/> when nothing was published yet. Snapshot mới nhất, hoặc <see langword="null"/> khi chưa đăng gì.</returns>
    public AnnotSnapshot? TryGet()
    {
        lock (_sync) return _latest;
    }
}

/// <summary>
/// Annotated frame: JPEG bytes plus the output size. Khung đã ghi chú: byte JPEG kèm kích thước
/// đầu ra.
/// </summary>
/// <param name="Jpeg">JPEG-encoded frame (quality 70). Khung mã hoá JPEG (quality 70).</param>
/// <param name="Width">Output width in pixels (≤ 1280). Chiều rộng đầu ra (pixel, ≤ 1280).</param>
/// <param name="Height">Output height in pixels. Chiều cao đầu ra (pixel).</param>
public sealed record AnnotatedFrame(byte[] Jpeg, int Width, int Height);

/// <summary>
/// Renders a captured frame with its text boxes and translations as <see cref="AnnotatedFrame"/>
/// (single source of truth for both the local preview and the virtual camera). Vẽ khung chụp
/// kèm box text và bản dịch thành <see cref="AnnotatedFrame"/> (nguồn sự thật duy nhất cho
/// preview local và camera ảo).
/// </summary>
public interface IFrameAnnotator
{
    /// <summary>
    /// Annotates one frame: downscale to at most 1280 wide, draw boxes/translations when a
    /// snapshot is available, encode JPEG quality 70. Ghi chú một khung: thu nhỏ về tối đa 1280
    /// rộng, vẽ box/bản dịch khi có snapshot, mã hoá JPEG quality 70.
    /// </summary>
    /// <param name="frame">Captured BGRA frame (caller keeps ownership; it is not copied).
    /// Khung BGRA đã chụp (người gọi giữ quyền sở hữu; khung không được sao chép).</param>
    /// <param name="snapshot">Text regions to draw, or <see langword="null"/> for a plain frame. Vùng text cần vẽ, hoặc <see langword="null"/> cho khung thường.</param>
    /// <returns>The annotated frame (JPEG bytes + output size). Khung đã ghi chú (bytes JPEG + kích thước đầu ra).</returns>
    AnnotatedFrame Annotate(Frame frame, AnnotSnapshot? snapshot);
}
