namespace ReTran.Core.Serve;

/// <summary>
/// Result of one batched translation-cache lookup: texts already cached and texts still missing.
/// Kết quả một lượt tra cache dịch thuật theo lô: text đã có trong cache và text còn thiếu.
/// </summary>
/// <param name="Hits">Cached translations keyed by source text. Bản dịch đã cache, key theo text nguồn.</param>
/// <param name="Misses">Source texts absent from the cache. Text nguồn chưa có trong cache.</param>
public sealed record CacheLookupResult(IReadOnlyDictionary<string, string> Hits, IReadOnlyList<string> Misses);

/// <summary>
/// Batched translation-cache channel between the Core engine and the App (SQLite) over IPC.
/// Kênh cache dịch thuật theo lô giữa lõi Core và App (SQLite) qua IPC.
/// </summary>
public interface ICacheChannel
{
    /// <summary>
    /// Looks up cached translations for a batch of source texts.
    /// Tra bản dịch đã cache cho một lô text nguồn.
    /// </summary>
    /// <param name="context">
    /// Content-context key, e.g. <c>screen:0</c> or <c>notepad.exe</c>. Key ngữ cảnh nội dung,
    /// vd <c>screen:0</c> hay <c>notepad.exe</c>.
    /// </param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="texts">Source texts to look up. Text nguồn cần tra.</param>
    /// <param name="ct">Token used to cancel the lookup. Token dùng để hủy lượt tra.</param>
    /// <returns>
    /// The partition of <paramref name="texts"/> into cache hits and misses.
    /// Kết quả chia <paramref name="texts"/> thành các text trúng cache (hit) và thiếu (miss).
    /// </returns>
    /// <remarks>
    /// Implementations are expected to fail fast (short timeout); <see cref="TranslateCycleSink"/>
    /// treats any thrown exception as an all-miss result so the cycle never stalls.
    /// Triển khai nên fail nhanh (timeout ngắn); <see cref="TranslateCycleSink"/> coi mọi exception
    /// ném ra là all-miss để chu kỳ không bao giờ bị đình trệ.
    /// </remarks>
    Task<CacheLookupResult> LookupAsync(
        string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct);

    /// <summary>
    /// Stores newly translated texts into the App-owned cache (best-effort upsert).
    /// Ghi các text vừa dịch vào cache do App sở hữu (best-effort upsert).
    /// </summary>
    /// <param name="context">
    /// Content-context key the translations belong to. Key ngữ cảnh nội dung mà bản dịch thuộc về.
    /// </param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="entries">Text → translation pairs to store. Cặp text → bản dịch cần lưu.</param>
    /// <param name="ct">Token used to cancel the write. Token dùng để hủy việc ghi.</param>
    /// <returns>A task that completes once the entries were handed off. Task hoàn tất khi đã chuyển xong các dòng.</returns>
    Task PutAsync(
        string context, string src, string dst,
        IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct);
}
