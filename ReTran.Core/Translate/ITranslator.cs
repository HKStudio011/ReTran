namespace ReTran.Core.Translate;

/// <summary>
/// A translation provider that converts text between language codes.
/// Nhà cung cấp dịch thuật, chuyển đổi văn bản giữa các mã ngôn ngữ.
/// </summary>
public interface ITranslator
{
    /// <summary>
    /// Stable provider identifier used for registry lookup.
    /// Mã định danh nhà cung cấp ổn định, dùng để tra trong registry.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Translates <paramref name="text"/> from <paramref name="src"/> to <paramref name="dst"/>.
    /// Dịch <paramref name="text"/> từ <paramref name="src"/> sang <paramref name="dst"/>.
    /// </summary>
    /// <param name="text">Source text to translate. Văn bản nguồn cần dịch.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>The translated text. Văn bản đã dịch.</returns>
    Task<string> TranslateAsync(string text, string src, string dst, CancellationToken ct = default);
}
