using DiscaScout.Application;
using DiscaScout.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Application.Tests;

/// <summary>
/// レンタル履歴由来CDのジャンル初期状態を検証する
/// </summary>
public sealed class RentalHistoryImportServiceGenreTests
{
    /// <summary>履歴HTMLにジャンルがないため仮文字列を保存せずGenreIdを未解決のまま作成することを確認する</summary>
    [Fact]
    public async Task ImportAsync_NewDisc_LeavesGenreIdNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var service = new RentalHistoryImportService(dbContext);
        await service.ImportAsync([new RentalHistoryImportEntry("12345", "作品", "アーティスト")]);

        var disc = await dbContext.Discs.SingleAsync();
        Assert.Null(disc.GenreId);
    }
}
