using Microsoft.EntityFrameworkCore;
using ReTran_App.Data;

namespace ReTran_App.Services;

/// <summary>
/// Result of one batched translation-cache lookup: texts already cached and texts still missing.
/// Kết quả một lượt tra cache dịch thuật theo lô: text đã có trong cache và text còn thiếu.
/// </summary>
/// <param name="Hits">Cached translations keyed by source text. Bản dịch đã cache, key theo text nguồn.</param>
/// <param name="Misses">Source texts absent from the cache, in input order. Text nguồn chưa có trong cache, theo thứ tự input.</param>
public sealed record CacheLookupResult(IReadOnlyDictionary<string, string> Hits, IReadOnlyList<string> Misses);

/// <summary>
/// App-owned SQLite translation cache: batched lookup and upsert over the unique key
/// (Context, Text, Src, Dst). The Core engine reaches it over IPC, never the database file.
/// Cache dịch thuật do App sở hữu (SQLite): tra theo lô và upsert trên key độc nhất
/// (Context, Text, Src, Dst). Lõi Core chỉ tới được qua IPC, không đụng trực tiếp file DB.
/// </summary>
/// <param name="db">Context used for all cache access. Context dùng cho mọi thao tác cache.</param>
public sealed class TranslationCache(RetranDbContext db)
{
    private readonly RetranDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Default database file (%LOCALAPPDATA%\ReTran\retran.db), for DI registration
    /// via <c>UseSqlite($"Data Source={path}")</c>.
    /// File database mặc định (%LOCALAPPDATA%\ReTran\retran.db) để đăng ký DI qua
    /// <c>UseSqlite($"Data Source={path}")</c>.
    /// </summary>
    public static string DefaultDbPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReTran", "retran.db");

    /// <summary>
    /// Looks up cached translations for a batch of source texts in one query.
    /// Tra bản dịch đã cache cho một lô text nguồn bằng đúng một truy vấn.
    /// </summary>
    /// <param name="context">Content-context key the texts belong to. Key ngữ cảnh nội dung mà các text thuộc về.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="texts">Source texts to look up; empty returns an empty result immediately. Text nguồn cần tra; rỗng trả kết quả rỗng ngay.</param>
    /// <param name="ct">Token used to cancel the query. Token dùng để hủy truy vấn.</param>
    /// <returns>
    /// The partition of <paramref name="texts"/> into hits and misses, misses in input order.
    /// Kết quả chia <paramref name="texts"/> thành hit và miss, miss giữ thứ tự input.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public async Task<CacheLookupResult> LookupAsync(
        string context, string src, string dst, IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
            return new CacheLookupResult(new Dictionary<string, string>(), Array.Empty<string>());

        string[] distinct = texts.Distinct().ToArray();
        List<TranslationEntry> rows = await _db.Translations
            .AsNoTracking()
            .Where(e => e.Context == context && e.Src == src && e.Dst == dst
                        && distinct.Contains(e.Text))
            .ToListAsync(ct);

        var byText = new Dictionary<string, string>(rows.Count);
        foreach (TranslationEntry row in rows)
            byText[row.Text] = row.Translated;

        var hits = new Dictionary<string, string>();
        var misses = new List<string>();
        foreach (string text in texts)
        {
            if (byText.TryGetValue(text, out string? translated))
                hits[text] = translated;
            else
                misses.Add(text);
        }
        return new CacheLookupResult(hits, misses);
    }

    /// <summary>
    /// Stores a batch of translations in one transaction: existing keys get their
    /// <see cref="TranslationEntry.Translated"/> updated, new keys are inserted.
    /// Lưu một lô bản dịch trong một transaction: key đã có thì cập nhật
    /// <see cref="TranslationEntry.Translated"/>, key chưa có thì chèn mới.
    /// </summary>
    /// <param name="context">Content-context key the entries belong to. Key ngữ cảnh nội dung mà các dòng thuộc về.</param>
    /// <param name="src">Source language code, or <c>auto</c>. Mã ngôn ngữ nguồn, hoặc <c>auto</c>.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="entries">Text → translation pairs to store. Cặp text → bản dịch cần lưu.</param>
    /// <param name="ct">Token used to cancel the write. Token dùng để hủy việc ghi.</param>
    /// <returns>A task that completes once the batch was committed. Task hoàn tất khi lô đã commit.</returns>
    /// <exception cref="ArgumentNullException">Thrown when any reference parameter is null. Ném khi tham số tham chiếu nào đó là null.</exception>
    public async Task PutAsync(
        string context, string src, string dst,
        IReadOnlyList<(string Text, string Translated)> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return;

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
            await _db.Database.BeginTransactionAsync(ct);

        string[] distinctTexts = entries.Select(e => e.Text).Distinct().ToArray();
        List<TranslationEntry> existing = await _db.Translations
            .Where(e => e.Context == context && e.Src == src && e.Dst == dst
                        && distinctTexts.Contains(e.Text))
            .ToListAsync(ct);

        // Also covers a text repeated inside this same batch: the pending insert is reused.
        var byText = new Dictionary<string, TranslationEntry>(existing.Count);
        foreach (TranslationEntry row in existing)
            byText[row.Text] = row;

        foreach ((string text, string translated) in entries)
        {
            if (byText.TryGetValue(text, out TranslationEntry? row))
            {
                row.Translated = translated;
            }
            else
            {
                row = new TranslationEntry
                {
                    Context = context,
                    Text = text,
                    Src = src,
                    Dst = dst,
                    Translated = translated,
                    CreatedAtUtc = DateTime.UtcNow,
                };
                _db.Translations.Add(row);
                byText[text] = row;
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
    }
}
