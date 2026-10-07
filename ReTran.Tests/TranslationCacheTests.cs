using Microsoft.EntityFrameworkCore;
using ReTran_App.Data;
using ReTran_App.Services;
using Xunit;

public class TranslationCacheTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<RetranDbContext> _options;
    private readonly List<string> _tempFiles = new();

    public TranslationCacheTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"retran-cache-{Guid.NewGuid():N}.db");
        _tempFiles.Add(_dbPath);
        _options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={_dbPath};Pooling=False")
            .Options;
        using var db = new RetranDbContext(_options);
        db.Database.Migrate();
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
    public async Task Lookup_ReturnsHitsAndMisses()
    {
        await using var db = new RetranDbContext(_options);
        var cache = new TranslationCache(db);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("hello", "xin chào"), ("world", "thế giới") }, CancellationToken.None);

        CacheLookupResult empty = await cache.LookupAsync("notepad.exe", "en", "vi",
            Array.Empty<string>(), CancellationToken.None);
        Assert.Empty(empty.Hits);
        Assert.Empty(empty.Misses);

        CacheLookupResult result = await cache.LookupAsync("notepad.exe", "en", "vi",
            new[] { "hello", "missing one", "world", "missing two" }, CancellationToken.None);

        Assert.Equal(2, result.Hits.Count);
        Assert.Equal("xin chào", result.Hits["hello"]);
        Assert.Equal("thế giới", result.Hits["world"]);
        Assert.Equal(new[] { "missing one", "missing two" }, result.Misses);
    }

    [Fact]
    public async Task Lookup_DifferentContextOrLang_IsMiss()
    {
        await using var db = new RetranDbContext(_options);
        var cache = new TranslationCache(db);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("hello", "xin chào") }, CancellationToken.None);

        CacheLookupResult otherContext = await cache.LookupAsync("chrome.exe", "en", "vi",
            new[] { "hello" }, CancellationToken.None);
        Assert.Empty(otherContext.Hits);
        Assert.Equal(new[] { "hello" }, otherContext.Misses);

        CacheLookupResult otherLang = await cache.LookupAsync("notepad.exe", "en", "ja",
            new[] { "hello" }, CancellationToken.None);
        Assert.Empty(otherLang.Hits);
        Assert.Equal(new[] { "hello" }, otherLang.Misses);
    }

    [Fact]
    public async Task Put_Upsert_UpdatesTranslated()
    {
        await using var db = new RetranDbContext(_options);
        var cache = new TranslationCache(db);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("hello", "xin chào") }, CancellationToken.None);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("hello", "chào bạn") }, CancellationToken.None);

        Assert.Equal(1, await db.Translations.CountAsync());
        string translated = await db.Translations
            .Where(e => e.Text == "hello")
            .Select(e => e.Translated)
            .SingleAsync();
        Assert.Equal("chào bạn", translated);
    }

    [Fact]
    public async Task Put_DifferentKeys_Coexist()
    {
        await using var db = new RetranDbContext(_options);
        var cache = new TranslationCache(db);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("hello", "xin chào") }, CancellationToken.None);
        await cache.PutAsync("chrome.exe", "en", "vi",
            new[] { ("hello", "chào") }, CancellationToken.None);
        await cache.PutAsync("notepad.exe", "en", "ja",
            new[] { ("hello", "こんにちは") }, CancellationToken.None);
        await cache.PutAsync("notepad.exe", "en", "vi",
            new[] { ("world", "thế giới") }, CancellationToken.None);

        Assert.Equal(4, await db.Translations.CountAsync());

        CacheLookupResult result = await cache.LookupAsync("chrome.exe", "en", "vi",
            new[] { "hello" }, CancellationToken.None);
        Assert.Equal("chào", result.Hits["hello"]);
    }

    [Fact]
    public async Task Migrate_FreshFile_CreatesSchema()
    {
        string freshPath = Path.Combine(Path.GetTempPath(), $"retran-fresh-{Guid.NewGuid():N}.db");
        _tempFiles.Add(freshPath);
        Assert.False(File.Exists(freshPath));

        var options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={freshPath};Pooling=False")
            .Options;
        await using var db = new RetranDbContext(options);
        await db.Database.MigrateAsync();

        db.Translations.Add(new TranslationEntry
        {
            Context = "game.exe",
            Text = "quest complete",
            Src = "en",
            Dst = "vi",
            Translated = "hoàn thành nhiệm vụ",
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.Translations.CountAsync());
    }
}
