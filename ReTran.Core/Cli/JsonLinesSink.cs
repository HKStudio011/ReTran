using System.Globalization;
using System.Text.Json.Nodes;

namespace ReTran.Core.Cli;

/// <summary>
/// Writes each OCR cycle as one JSON line in the <c>ocr loop</c> M3 contract, then flushes.
/// Ghi mỗi chu kỳ OCR thành một dòng JSON theo contract M3 <c>ocr loop</c>, rồi flush.
/// </summary>
/// <param name="stdout">Writer for the JSON lines (stdout in production). Nơi ghi các dòng JSON (stdout khi chạy thật).</param>
public sealed class JsonLinesSink(TextWriter stdout) : ICycleSink
{
    /// <summary>
    /// Builds the M3 contract line (<c>seq</c>, <c>at</c>, <c>new_texts</c>, <c>spots_count</c>, <c>elapsed_ms</c>)
    /// and writes it followed by a flush.
    /// Dựng dòng theo contract M3 (<c>seq</c>, <c>at</c>, <c>new_texts</c>, <c>spots_count</c>, <c>elapsed_ms</c>)
    /// rồi ghi ra và flush.
    /// </summary>
    /// <param name="seq">1-based cycle number. Số chu kỳ tính từ 1.</param>
    /// <param name="frameWidth">Captured frame width in pixels. Chiều rộng khung hình (pixel).</param>
    /// <param name="frameHeight">Captured frame height in pixels. Chiều cao khung hình (pixel).</param>
    /// <param name="spotResult">
    /// Pipeline result: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// Kết quả pipeline: <c>{spots:[{box,text,score}], elapsed_ms, new_texts:[...]}</c>.
    /// </param>
    /// <param name="ct">Cancellation token for the cycle. Token hủy của chu kỳ.</param>
    /// <returns>A completed task (the write is synchronous). Task đã hoàn tất (viết là đồng bộ).</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="stdout"/> or <paramref name="spotResult"/> is null.
    /// Ném khi <paramref name="stdout"/> hoặc <paramref name="spotResult"/> là null.
    /// </exception>
    public Task OnCycleAsync(long seq, int frameWidth, int frameHeight, JsonObject spotResult, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(spotResult);
        var line = new JsonObject
        {
            ["seq"] = seq,
            ["at"] = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["new_texts"] = spotResult["new_texts"]?.DeepClone() ?? new JsonArray(),
            ["spots_count"] = spotResult["spots"] is JsonArray spots ? spots.Count : 0,
            ["elapsed_ms"] = spotResult["elapsed_ms"]?.DeepClone() ?? 0,
        };
        stdout.WriteLine(line.ToJsonString());
        stdout.Flush();
        return Task.CompletedTask;
    }
}
