using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ReTran_App.Services;

namespace ReTran_App.Data;

/// <summary>
/// Design-time factory so `dotnet ef` can create the context without a running MAUI host.
/// Factory thời điểm thiết kế để `dotnet ef` tạo context mà không cần host MAUI đang chạy.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RetranDbContext>
{
    /// <summary>
    /// Creates a context over the default database path (design-time only; no connection is opened).
    /// Tạo context trên đường dẫn DB mặc định (chỉ thời điểm thiết kế; không mở kết nối).
    /// </summary>
    /// <param name="args">Arguments passed to the EF tools. Tham số được truyền cho EF tools.</param>
    /// <returns>A context using the default SQLite file. Context dùng file SQLite mặc định.</returns>
    public RetranDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RetranDbContext>()
            .UseSqlite($"Data Source={TranslationCache.DefaultDbPath}")
            .Options;
        return new RetranDbContext(options);
    }
}
