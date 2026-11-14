using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReTran_App.Data;
using ReTran_App.Services;
using Xunit;

public class ProfileStoreTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private DbContextOptions<RetranDbContext> NewOptions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retran-profiles-{Guid.NewGuid():N}.db");
        _tempFiles.Add(path);
        var options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .Options;
        using var db = new RetranDbContext(options);
        db.Database.Migrate();
        return options;
    }

    public void Dispose()
    {
        foreach (string path in _tempFiles)
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact]
    public async Task GetContexts_GroupsAndCounts()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("a.exe", "en", "vi", new[] { ("one", "một"), ("two", "hai") }, CancellationToken.None);
            await cache.PutAsync("b.exe", "en", "vi", new[] { ("three", "ba") }, CancellationToken.None);
        }

        await using (var db = new RetranDbContext(options))
        {
            var store = new ProfileStore(db);
            IReadOnlyList<ProfileContextInfo> contexts = await store.GetContextsAsync(CancellationToken.None);

            Assert.Equal(new[] { "a.exe", "b.exe" }, contexts.Select(c => c.Context));
            Assert.Equal(new[] { 2, 1 }, contexts.Select(c => c.Count));
        }
    }

    [Fact]
    public async Task GetEntries_SearchAndLimit()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("x.exe", "en", "vi",
                new[] { ("hello", "xin chào"), ("world", "thế giới"), ("hollow", "rỗng") }, CancellationToken.None);
        }

        await using (var db = new RetranDbContext(options))
        {
            var store = new ProfileStore(db);

            // Search covers BOTH columns, so "hell" (source-only) pins the text side without the
            // translated-side matches. Tìm kiếm gồm CẢ hai cột nên "hell" (chỉ ở cột nguồn) ghim
            // được phía text mà không dính match bên bản dịch.
            IReadOnlyList<ProfileEntryInfo> byText = await store.GetEntriesAsync("x.exe", "hell", 200, CancellationToken.None);
            Assert.Equal("hello", Assert.Single(byText).Text);

            IReadOnlyList<ProfileEntryInfo> byTranslated = await store.GetEntriesAsync("x.exe", "thế", 200, CancellationToken.None);
            ProfileEntryInfo hit = Assert.Single(byTranslated);
            Assert.Equal("world", hit.Text);
            Assert.Equal("thế giới", hit.Translated);
            Assert.Equal("en", hit.Src);
            Assert.Equal("vi", hit.Dst);

            IReadOnlyList<ProfileEntryInfo> limited = await store.GetEntriesAsync("x.exe", null, 1, CancellationToken.None);
            Assert.Single(limited);
        }
    }

    [Fact]
    public async Task UpdateEntry_ChangesTranslated()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("x.exe", "en", "vi", new[] { ("hello", "sai") }, CancellationToken.None);
            var store = new ProfileStore(db);

            Assert.True(await store.UpdateEntryAsync("x.exe", "hello", "en", "vi", "xin chào", CancellationToken.None));

            IReadOnlyList<ProfileEntryInfo> entries = await store.GetEntriesAsync("x.exe", null, 10, CancellationToken.None);
            Assert.Equal("xin chào", Assert.Single(entries).Translated);
        }
    }

    [Fact]
    public async Task Update_UnknownKey_ReturnsFalse()
    {
        await using var db = new RetranDbContext(NewOptions());
        var store = new ProfileStore(db);
        Assert.False(await store.UpdateEntryAsync("x.exe", "nope", "en", "vi", "x", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteEntry_Removes()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("x.exe", "en", "vi", new[] { ("hello", "xin chào") }, CancellationToken.None);
            var store = new ProfileStore(db);

            Assert.True(await store.DeleteEntryAsync("x.exe", "hello", "en", "vi", CancellationToken.None));
            Assert.Empty(await store.GetEntriesAsync("x.exe", null, 10, CancellationToken.None));
            Assert.False(await store.DeleteEntryAsync("x.exe", "hello", "en", "vi", CancellationToken.None));
        }
    }

    [Fact]
    public async Task DeleteContext_LeavesOtherContexts()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("a.exe", "en", "vi", new[] { ("one", "một"), ("two", "hai") }, CancellationToken.None);
            await cache.PutAsync("b.exe", "en", "vi", new[] { ("three", "ba") }, CancellationToken.None);
            var store = new ProfileStore(db);

            Assert.Equal(2, await store.DeleteContextAsync("a.exe", CancellationToken.None));
            Assert.Equal(new[] { "b.exe" }, (await store.GetContextsAsync(CancellationToken.None)).Select(c => c.Context));
        }
    }

    [Fact]
    public async Task ExportImport_RoundTrip_PreservesAll()
    {
        DbContextOptions<RetranDbContext> source = NewOptions();
        await using (var db = new RetranDbContext(source))
        {
            var cache = new TranslationCache(db);
            await cache.PutAsync("a.exe", "en", "vi", new[] { ("hello 🎮", "xin chào 🎮"), ("world", "thế giới") }, CancellationToken.None);
            await cache.PutAsync("screen:0", "ja", "vi", new[] { ("こんにちは", "xin chào") }, CancellationToken.None);
        }

        using var export = new MemoryStream();
        await using (var db = new RetranDbContext(source))
        {
            await new ProfileStore(db).ExportAsync(export, null, CancellationToken.None);
        }

        export.Position = 0;
        await using (var db = new RetranDbContext(NewOptions()))
        {
            ProfileImportResult result = await new ProfileStore(db).ImportAsync(export, CancellationToken.None);
            Assert.Equal(3, result.Added);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Skipped);

            var store = new ProfileStore(db);
            IReadOnlyList<ProfileEntryInfo> entries = await store.GetEntriesAsync("a.exe", "hello", 10, CancellationToken.None);
            ProfileEntryInfo hit = Assert.Single(entries);
            Assert.Equal("xin chào 🎮", hit.Translated);
        }
    }

    [Fact]
    public async Task Import_UpsertsExisting_NoDuplicateKey()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using var db = new RetranDbContext(options);
        var cache = new TranslationCache(db);
        await cache.PutAsync("x.exe", "en", "vi", new[] { ("hello", "cũ") }, CancellationToken.None);

        using var import = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"version":1,"entries":[{"context":"x.exe","src":"en","dst":"vi","text":"hello","translated":"mới"}]}"""));
        var store = new ProfileStore(db);
        ProfileImportResult result = await store.ImportAsync(import, CancellationToken.None);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Updated);
        ProfileEntryInfo hit = Assert.Single(await store.GetEntriesAsync("x.exe", null, 10, CancellationToken.None));
        Assert.Equal("mới", hit.Translated);
    }

    [Fact]
    public async Task Import_SkipsInvalidRows_CountsSkipped()
    {
        await using var db = new RetranDbContext(NewOptions());
        const string json = """
            {"version":1,"entries":[
                {"context":"x.exe","src":"en","dst":"vi","text":"ok","translated":"ổn"},
                {"context":"x.exe","src":"en","dst":"vi","text":"missing translated"},
                {"context":123,"src":"en","dst":"vi","text":"bad context","translated":"x"}
            ]}
            """;
        using var import = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));

        ProfileImportResult result = await new ProfileStore(db).ImportAsync(import, CancellationToken.None);

        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task Import_MalformedJson_Throws()
    {
        await using var db = new RetranDbContext(NewOptions());
        using var import = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not json at all"));

        await Assert.ThrowsAsync<FormatException>(
            () => new ProfileStore(db).ImportAsync(import, CancellationToken.None));
    }

    [Fact]
    public async Task Update_WhileWriterActive_NoThrow()
    {
        DbContextOptions<RetranDbContext> options = NewOptions();
        await using (var db = new RetranDbContext(options))
        {
            var cache = new TranslationCache(db);
            var store = new ProfileStore(db);
            await cache.PutAsync("x.exe", "en", "vi", new[] { ("hello", "sai") }, CancellationToken.None);

            // Interleave a pipeline-style write (translate.put) with a profile edit; both go
            // through SQLite writes and must succeed without fighting over the file.
            // Xen kẽ một ghi kiểu pipeline (translate.put) với sửa profile; cả hai là ghi SQLite
            // và phải thành công mà không tranh chấp file.
            await cache.PutAsync("x.exe", "en", "vi", new[] { ("other", "khác") }, CancellationToken.None);
            Assert.True(await store.UpdateEntryAsync("x.exe", "hello", "en", "vi", "xin chào", CancellationToken.None));
            await cache.PutAsync("x.exe", "en", "vi", new[] { ("third", "ba") }, CancellationToken.None);
            Assert.True(await store.UpdateEntryAsync("x.exe", "other", "en", "vi", "khác nữa", CancellationToken.None));

            Assert.Equal(3, (await store.GetEntriesAsync("x.exe", null, 10, CancellationToken.None)).Count);
        }
    }
}
