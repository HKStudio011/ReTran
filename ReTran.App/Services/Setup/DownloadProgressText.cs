namespace ReTran_App.Services.Setup;

/// <summary>
/// Byte-level progress of one in-flight model download — the wizard vòng 2 inputs for % + ETA.
/// Tiến độ theo byte của một lượt tải model — đầu vào % + ETA của wizard vòng 2.
/// </summary>
/// <param name="ReceivedBytes">Bytes received so far. Số byte đã nhận.</param>
/// <param name="TotalBytes">Total bytes, or 0 when unknown. Tổng byte, hoặc 0 khi chưa biết.</param>
/// <param name="BytesPerSec">Current speed in bytes/second, or <see langword="null"/>. Tốc độ hiện tại (byte/giây), hoặc <see langword="null"/>.</param>
public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes, double? BytesPerSec);

/// <summary>
/// Pure formatting math for download progress: percent and remaining time. No i18n strings here —
/// the UI combines the numbers with its own keys. Tolerant by design: an unknown total, a missing
/// speed or out-of-range byte counts yield <see langword="null"/> parts instead of NaN or negatives.
/// Toán thuần cho tiến độ tải: phần trăm và thời gian còn lại. Không chứa chuỗi i18n — UI tự ghép
/// với key của nó. Chịu dữ liệu xấu theo thiết kế: thiếu tổng, thiếu tốc độ hoặc số byte vô lý đều
/// cho <see langword="null"/> thay vì NaN hay số âm.
/// </summary>
public static class DownloadProgressText
{
    /// <summary>
    /// Computes the percent (0..100) and the ETA from raw counters; each part is
    /// <see langword="null"/> when its inputs are not usable (unknown total, negative bytes, no
    /// speed, or the download already complete).
    /// Tính phần trăm (0..100) và ETA từ các bộ đếm thô; mỗi phần là <see langword="null"/> khi đầu
    /// vào không dùng được (chưa biết tổng, byte âm, không có tốc độ, hoặc đã tải xong).
    /// </summary>
    /// <param name="receivedBytes">Bytes received. Số byte đã nhận.</param>
    /// <param name="totalBytes">Total bytes, or 0 when unknown. Tổng byte, hoặc 0 khi chưa biết.</param>
    /// <param name="bytesPerSec">Current speed, or <see langword="null"/>. Tốc độ hiện tại, hoặc <see langword="null"/>.</param>
    /// <returns>Percent and ETA, each possibly <see langword="null"/>. Phần trăm và ETA, mỗi phần có thể <see langword="null"/>.</returns>
    public static (int? Percent, TimeSpan? Eta) Format(long receivedBytes, long totalBytes, double? bytesPerSec)
    {
        if (totalBytes <= 0 || receivedBytes < 0) return (null, null);

        long clamped = Math.Min(receivedBytes, totalBytes);
        int percent = (int)(clamped * 100 / totalBytes);

        TimeSpan? eta = bytesPerSec is > 0 && clamped < totalBytes
            ? TimeSpan.FromSeconds((totalBytes - clamped) / bytesPerSec.Value)
            : null;
        return (percent, eta);
    }
}
