namespace ReTran_App.Data;

/// <summary>
/// One cached translation row, uniquely keyed by (Context, Text, Src, Dst).
/// Một dòng bản dịch được cache, key độc nhất theo (Context, Text, Src, Dst).
/// </summary>
public sealed class TranslationEntry
{
    /// <summary>Surrogate primary key. Khóa chính giả danh.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Content-context key the entry belongs to, e.g. <c>screen:0</c> or <c>notepad.exe</c>.
    /// Key ngữ cảnh nội dung mà dòng này thuộc về, vd <c>screen:0</c> hay <c>notepad.exe</c>.
    /// </summary>
    public string Context { get; set; } = "";

    /// <summary>Source text as recognized by OCR. Text nguồn do OCR nhận diện.</summary>
    public string Text { get; set; } = "";

    /// <summary>Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</summary>
    public string Src { get; set; } = "";

    /// <summary>Target language code. Mã ngôn ngữ đích.</summary>
    public string Dst { get; set; } = "";

    /// <summary>Cached translation of <see cref="Text"/>. Bản dịch đã cache của <see cref="Text"/>.</summary>
    public string Translated { get; set; } = "";

    /// <summary>
    /// UTC timestamp of the first insert; unchanged on later updates.
    /// Dấu thời gian UTC lúc ghi lần đầu; không đổi khi cập nhật về sau.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }
}
