using System.Text.Json.Nodes;

namespace ReTran.Core.Cli;

/// <summary>
/// Receives each completed OCR cycle from <see cref="OcrLoop"/> and forwards it downstream.
/// Nhận mỗi chu kỳ OCR hoàn tất từ <see cref="OcrLoop"/> và chuyển tiếp xuống hạ nguồn.
/// </summary>
public interface ICycleSink
{
    /// <summary>
    /// Handles one processed cycle: frame <paramref name="seq"/> with its spot result.
    /// Xử lý một chu kỳ đã xử lý: khung <paramref name="seq"/> kèm kết quả spot của nó.
    /// </summary>
    /// <param name="seq">1-based cycle number. Số chu kỳ tính từ 1.</param>
    /// <param name="frameWidth">Captured frame width in pixels. Chiều rộng khung hình (pixel).</param>
    /// <param name="frameHeight">Captured frame height in pixels. Chiều cao khung hình (pixel).</param>
    /// <param name="spotResult">
    /// Pipeline result: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// Kết quả pipeline: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// </param>
    /// <param name="ct">Cancellation token for the cycle. Token hủy của chu kỳ.</param>
    /// <returns>A task that completes when the cycle has been handled. Task hoàn tất khi xử lý xong chu kỳ.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="spotResult"/> is null.</exception>
    Task OnCycleAsync(long seq, int frameWidth, int frameHeight, JsonObject spotResult, CancellationToken ct);
}
