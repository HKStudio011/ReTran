namespace ReTran.Core.Ocr;

/// <summary>
/// Tracks texts seen during this session and returns only newly appearing ones.
/// Theo dõi text đã thấy trong phiên và chỉ trả về những text mới xuất hiện.
/// </summary>
public sealed class OcrDiff
{
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Feeds the current texts and returns those never seen before in this session.
    /// Cho các text hiện tại vào và trả về những text chưa từng thấy trong phiên này.
    /// </summary>
    /// <param name="texts">Recognized texts from one OCR pass. Các text nhận được từ một lần OCR.</param>
    /// <returns>New non-blank texts in input order; all are marked seen. Các text mới không rỗng theo thứ tự đầu vào; tất cả được đánh dấu đã thấy.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="texts"/> is null.</exception>
    public IReadOnlyList<string> Feed(IEnumerable<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var fresh = new List<string>();
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (_seen.Add(t)) fresh.Add(t);
        }
        return fresh;
    }
}
