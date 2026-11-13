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

    /// <summary>
    /// Translates several texts from <paramref name="src"/> to <paramref name="dst"/>.
    /// Dịch nhiều văn bản từ <paramref name="src"/> sang <paramref name="dst"/>.
    /// </summary>
    /// <param name="texts">Source texts to translate. Các văn bản nguồn cần dịch.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the request. Token dùng để hủy yêu cầu.</param>
    /// <returns>
    /// One translated text per input, in the same order as <paramref name="texts"/>.
    /// Mỗi văn bản nguồn cho một kết quả đã dịch, giữ đúng thứ tự như <paramref name="texts"/>.
    /// </returns>
    /// <exception cref="Exception">
    /// Thrown when any item cannot be translated; no partial result is returned.
    /// Ném ra khi bất kỳ văn bản nào không dịch được; không trả về kết quả dở dang.
    /// </exception>
    /// <remarks>
    /// The default implementation translates each text independently via <see cref="TranslateAsync"/>.
    /// Providers that can translate a batch in fewer round-trips should override this member.
    /// Triển khai mặc định dịch từng văn bản riêng lẻ qua <see cref="TranslateAsync"/>.
    /// Nhà cung cấp có thể dịch cả lô trong ít lượt gọi hơn nên ghi đè phương thức này.
    /// </remarks>
    async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts, string src, string dst, CancellationToken ct = default)
        => await Task.WhenAll(texts.Select(t => TranslateAsync(t, src, dst, ct))).ConfigureAwait(false);
}
