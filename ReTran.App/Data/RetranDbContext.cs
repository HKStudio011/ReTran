using Microsoft.EntityFrameworkCore;

namespace ReTran_App.Data;

/// <summary>
/// SQLite-backed DbContext for the App-owned translation cache (%LOCALAPPDATA%\ReTran\retran.db).
/// DbContext dùng SQLite cho cache dịch thuật do App sở hữu (%LOCALAPPDATA%\ReTran\retran.db).
/// </summary>
/// <param name="options">Context options for the configured provider. Tùy chọn context với provider đã cấu hình.</param>
public sealed class RetranDbContext(DbContextOptions<RetranDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Cached translation rows. Các dòng bản dịch đã cache.
    /// </summary>
    public DbSet<TranslationEntry> Translations => Set<TranslationEntry>();

    /// <summary>
    /// Configures the unique cache key (Context, Text, Src, Dst).
    /// Cấu hình key cache độc nhất (Context, Text, Src, Dst).
    /// </summary>
    /// <param name="modelBuilder">Builder used to configure entity mappings. Builder dùng để cấu hình ánh xạ entity.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TranslationEntry>(entity =>
        {
            entity.HasIndex(e => new { e.Context, e.Text, e.Src, e.Dst }).IsUnique();
        });
    }
}
