using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// 永続DBを破棄せず起動時Migrationで初期スキーマを作成できることを検証する
/// </summary>
public sealed class MigrationTests
{
    [Fact]
    public async Task MigrateAsync_空DBへ初期スキーマを作成する()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.MigrateAsync();

        var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync();
        Assert.Empty(pendingMigrations);

        // 代表テーブルだけでなく設定行も実際に読み書きし、Migrationと現在モデルの型対応を確認する。
        var scheduleStore = new ScrapeScheduleStore(dbContext);
        var settings = await scheduleStore.GetAsync();
        Assert.False(settings.IsEnabled);
        Assert.Equal(DayOfWeek.Sunday, settings.DayOfWeek);
        Assert.Equal(new TimeOnly(4, 0), settings.LocalTime);
    }
}
