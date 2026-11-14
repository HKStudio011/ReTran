using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReTran_App.Data;

namespace ReTran_App.Services;

/// <summary>
/// One translation-cache context ("profile") with its entry count. Một ngữ cảnh cache dịch
/// ("profile") kèm số entry.
/// </summary>
/// <param name="Context">Content-context key, e.g. <c>notepad.exe</c> or <c>screen:0</c>. Key ngữ cảnh nội dung, vd <c>notepad.exe</c> hoặc <c>screen:0</c>.</param>
/// <param name="Count">Number of cached entries for the context. Số entry đã cache của ngữ cảnh.</param>
public sealed record ProfileContextInfo(string Context, int Count);

/// <summary>
/// One cached translation row as shown in the profiles sheet. Một dòng bản dịch đã cache như hiển
/// thị trong sheet profiles.
/// </summary>
/// <param name="Context">Content-context key. Key ngữ cảnh nội dung.</param>
/// <param name="Text">Source text. Văn bản nguồn.</param>
/// <param name="Src">Source language code. Mã ngôn ngữ nguồn.</param>
/// <param name="Dst">Target language code. Mã ngôn ngữ đích.</param>
/// <param name="Translated">Translated text. Văn bản dịch.</param>
/// <param name="CreatedAtUtc">Row creation time in UTC. Thời điểm tạo dòng (UTC).</param>
public sealed record ProfileEntryInfo(string Context, string Text, string Src, string Dst, string Translated, DateTime CreatedAtUtc);

/// <summary>
/// Outcome of one JSON import: inserted, updated and skipped rows. Kết quả một lượt import JSON:
/// số dòng thêm mới, cập nhật và bị bỏ.
/// </summary>
/// <param name="Added">Rows inserted. Số dòng được chèn.</param>
/// <param name="Updated">Existing keys whose translation was replaced. Số key đã có được thay bản dịch.</param>
/// <param name="Skipped">Invalid rows ignored (missing fields, non-string values, duplicates inside the file). Số dòng bỏ qua (thiếu field, sai kiểu, trùng trong file).</param>
public sealed record ProfileImportResult(int Added, int Updated, int Skipped);

/// <summary>
/// App-owned browse/edit surface over the translation cache, used by the profiles sheet. Shares
/// the app's <see cref="RetranDbContext"/> with <see cref="TranslationCache"/> but never joins the
/// pipeline's write paths — cache edits touch the database only.
/// Bề mặt xem/sửa cache dịch do App sở hữu, dùng cho sheet profiles. Dùng chung
/// <see cref="RetranDbContext"/> với <see cref="TranslationCache"/> nhưng không bao giờ nhập vào
/// đường ghi của pipeline — sửa cache chỉ đụng database.
/// </summary>
/// <param name="db">Context used for all profile access. Context dùng cho mọi thao tác profile.</param>
public sealed class ProfileStore(RetranDbContext db)
{
    /// <summary>Import/export file format version written by <see cref="ExportAsync"/>. Phiên bản định dạng file import/export do <see cref="ExportAsync"/> ghi.</summary>
    public const int FormatVersion = 1;

    private readonly RetranDbContext _db = db ?? throw new ArgumentNullException(nameof(db));

    /// <summary>
    /// Lists every cache context with its entry count, ordered by context name.
    /// Liệt kê mọi ngữ cảnh cache kèm số entry, sắp theo tên ngữ cảnh.
    /// </summary>
    /// <param name="ct">Token used to cancel the query. Token dùng để hủy truy vấn.</param>
    /// <returns>The contexts in ordinal order. Các ngữ cảnh theo thứ tự ordinal.</returns>
    public async Task<IReadOnlyList<ProfileContextInfo>> GetContextsAsync(CancellationToken ct)
    {
        var rows = await _db.Translations
            .AsNoTracking()
            .GroupBy(e => e.Context)
            .Select(g => new { Context = g.Key, Count = g.Count() })
            .OrderBy(x => x.Context)
            .ToListAsync(ct);
        return rows.Select(x => new ProfileContextInfo(x.Context, x.Count)).ToList();
    }

    /// <summary>
    /// Lists entries of one context, newest first, optionally filtered by a substring of the
    /// source or translated text. Liệt kê entry của một ngữ cảnh, mới nhất trước, lọc tuỳ chọn
    /// theo chuỗi con của text nguồn hoặc bản dịch.
    /// </summary>
    /// <param name="context">Content-context key to list. Key ngữ cảnh cần liệt kê.</param>
    /// <param name="search">Substring filter, or <see langword="null"/> for all rows. Chuỗi con để lọc, hoặc <see langword="null"/> để lấy hết.</param>
    /// <param name="limit">Maximum rows returned. Số dòng tối đa trả về.</param>
    /// <param name="ct">Token used to cancel the query. Token dùng để hủy truy vấn.</param>
    /// <returns>The matching entries. Các entry khớp.</returns>
    public async Task<IReadOnlyList<ProfileEntryInfo>> GetEntriesAsync(
        string context, string? search, int limit, CancellationToken ct)
    {
        IQueryable<TranslationEntry> query = _db.Translations
            .AsNoTracking()
            .Where(e => e.Context == context);
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(e => e.Text.Contains(search) || e.Translated.Contains(search));
        }

        List<TranslationEntry> rows = await query
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenBy(e => e.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(ct);
        return rows
            .Select(e => new ProfileEntryInfo(e.Context, e.Text, e.Src, e.Dst, e.Translated, e.CreatedAtUtc))
            .ToList();
    }

    /// <summary>
    /// Replaces the translation of one cache row. Thay bản dịch của một dòng cache.
    /// </summary>
    /// <param name="context">Content-context key of the row. Key ngữ cảnh của dòng.</param>
    /// <param name="text">Source text of the row. Text nguồn của dòng.</param>
    /// <param name="src">Source language code. Mã ngôn ngữ nguồn.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="translated">New translation to store. Bản dịch mới cần lưu.</param>
    /// <param name="ct">Token used to cancel the write. Token dùng để hủy việc ghi.</param>
    /// <returns><see langword="true"/> when the row existed and was updated. <see langword="true"/> khi dòng tồn tại và đã cập nhật.</returns>
    public async Task<bool> UpdateEntryAsync(
        string context, string text, string src, string dst, string translated, CancellationToken ct)
    {
        TranslationEntry? row = await FindAsync(context, text, src, dst, ct);
        if (row is null) return false;
        row.Translated = translated;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>
    /// Deletes one cache row. Xoá một dòng cache.
    /// </summary>
    /// <param name="context">Content-context key of the row. Key ngữ cảnh của dòng.</param>
    /// <param name="text">Source text of the row. Text nguồn của dòng.</param>
    /// <param name="src">Source language code. Mã ngôn ngữ nguồn.</param>
    /// <param name="dst">Target language code. Mã ngôn ngữ đích.</param>
    /// <param name="ct">Token used to cancel the write. Token dùng để hủy việc ghi.</param>
    /// <returns><see langword="true"/> when the row existed and was deleted. <see langword="true"/> khi dòng tồn tại và đã xoá.</returns>
    public async Task<bool> DeleteEntryAsync(
        string context, string text, string src, string dst, CancellationToken ct)
    {
        TranslationEntry? row = await FindAsync(context, text, src, dst, ct);
        if (row is null) return false;
        _db.Translations.Remove(row);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>
    /// Deletes every cache row of one context. Xoá toàn bộ dòng cache của một ngữ cảnh.
    /// </summary>
    /// <param name="context">Content-context key to clear. Key ngữ cảnh cần xoá.</param>
    /// <param name="ct">Token used to cancel the write. Token dùng để hủy việc ghi.</param>
    /// <returns>The number of deleted rows. Số dòng đã xoá.</returns>
    public async Task<int> DeleteContextAsync(string context, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        return await _db.Translations
            .Where(e => e.Context == context)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Streams the cache to versioned JSON: one object per row, writing rows as they are read so a
    /// large cache never lands in memory at once. Xuất cache ra JSON có phiên bản: mỗi dòng một
    /// object, ghi dần theo lúc đọc nên cache lớn không cần nằm trọn trong RAM.
    /// </summary>
    /// <param name="output">Destination stream; left open. Stream đích; không bị đóng.</param>
    /// <param name="context">Only export this context, or <see langword="null"/> for everything. Chỉ xuất ngữ cảnh này, hoặc <see langword="null"/> để xuất tất cả.</param>
    /// <param name="ct">Token used to cancel the export. Token dùng để hủy xuất.</param>
    public async Task ExportAsync(Stream output, string? context, CancellationToken ct)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteNumber("version", FormatVersion);
        writer.WriteString("exportedAtUtc", DateTime.UtcNow.ToString("O"));
        writer.WriteStartArray("entries");

        IQueryable<TranslationEntry> query = _db.Translations.AsNoTracking();
        if (context is not null) query = query.Where(e => e.Context == context);
        await foreach (TranslationEntry e in query
            .OrderBy(e => e.Context).ThenBy(e => e.Text)
            .AsAsyncEnumerable().WithCancellation(ct))
        {
            writer.WriteStartObject();
            writer.WriteString("context", e.Context);
            writer.WriteString("src", e.Src);
            writer.WriteString("dst", e.Dst);
            writer.WriteString("text", e.Text);
            writer.WriteString("translated", e.Translated);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// Imports rows from the same JSON format <see cref="ExportAsync"/> writes, upserting on the
    /// unique key <c>(Context, Text, Src, Dst)</c>. Rows with missing or non-string fields are
    /// skipped and counted; a duplicate key inside the file keeps the last row and counts the
    /// earlier one as skipped. Nhập dòng từ đúng định dạng JSON mà <see cref="ExportAsync"/> ghi,
    /// upsert theo key độc nhất <c>(Context, Text, Src, Dst)</c>. Dòng thiếu field hoặc sai kiểu
    /// bị bỏ và đếm; key trùng trong file giữ dòng cuối, dòng trước tính là bỏ.
    /// </summary>
    /// <param name="input">JSON stream to read. Stream JSON cần đọc.</param>
    /// <param name="ct">Token used to cancel the import. Token dùng để hủy import.</param>
    /// <returns>Insert/update/skip counts. Số liệu thêm/cập nhật/bỏ.</returns>
    /// <exception cref="FormatException">Thrown when the stream is not valid JSON or lacks the entries array. Khi stream không phải JSON hợp lệ hoặc thiếu mảng entries.</exception>
    public async Task<ProfileImportResult> ImportAsync(Stream input, CancellationToken ct)
    {
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(input, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Profiles import file is not valid JSON.", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("entries", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Profiles import file does not contain an 'entries' array.");
            }

            // Collapse duplicate keys inside the file first (last row wins) so the unique index
            // cannot trip on rows inserted within this same run.
            // Gộp key trùng trong file trước (dòng cuối thắng) để unique index không vỡ vì các
            // dòng chèn trong cùng lượt này.
            var ordered = new List<(string Context, string Src, string Dst, string Text, string Translated)>();
            var indexByKey = new Dictionary<(string, string, string, string), int>();
            int skipped = 0;
            foreach (JsonElement item in entries.EnumerateArray())
            {
                if (!TryReadEntry(item, out var entry))
                {
                    skipped++;
                    continue;
                }
                var key = (entry.Context, entry.Src, entry.Dst, entry.Text);
                if (indexByKey.TryGetValue(key, out int at))
                {
                    ordered[at] = entry;
                    skipped++;
                }
                else
                {
                    indexByKey[key] = ordered.Count;
                    ordered.Add(entry);
                }
            }

            int added = 0, updated = 0;
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx =
                await _db.Database.BeginTransactionAsync(ct);
            foreach (var entry in ordered)
            {
                TranslationEntry? row = await FindAsync(entry.Context, entry.Text, entry.Src, entry.Dst, ct);
                if (row is null)
                {
                    _db.Translations.Add(new TranslationEntry
                    {
                        Context = entry.Context,
                        Text = entry.Text,
                        Src = entry.Src,
                        Dst = entry.Dst,
                        Translated = entry.Translated,
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                    added++;
                }
                else
                {
                    row.Translated = entry.Translated;
                    updated++;
                }
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
            return new ProfileImportResult(added, updated, skipped);
        }
    }

    private Task<TranslationEntry?> FindAsync(string context, string text, string src, string dst, CancellationToken ct) =>
        _db.Translations.FirstOrDefaultAsync(
            e => e.Context == context && e.Text == text && e.Src == src && e.Dst == dst, ct);

    private static bool TryReadEntry(
        JsonElement item,
        out (string Context, string Src, string Dst, string Text, string Translated) entry)
    {
        entry = default;
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (!TryReadString(item, "context", out string context)) return false;
        if (!TryReadString(item, "src", out string src)) return false;
        if (!TryReadString(item, "dst", out string dst)) return false;
        if (!TryReadString(item, "text", out string text)) return false;
        if (!TryReadString(item, "translated", out string translated)) return false;
        entry = (context, src, dst, text, translated);
        return true;
    }

    private static bool TryReadString(JsonElement obj, string name, out string value)
    {
        value = string.Empty;
        if (!obj.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString()!;
        return !string.IsNullOrEmpty(value);
    }
}
