using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// 永続DBを破棄せず起動時Migrationで最新スキーマを作成できることを検証する
/// </summary>
public sealed class MigrationTests
{
    [Fact]
    public async Task MigrateAsync_空DBへ最新スキーマを作成する()
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

        var guardStore = new ScrapeGuardStore(dbContext);
        var enabledAt = new DateTime(2026, 8, 30, 3, 0, 0, DateTimeKind.Utc);
        await guardStore.EnableCountDropOverrideAsync(ScrapeCategory.New, enabledAt);
        dbContext.ChangeTracker.Clear();
        var guard = await guardStore.GetAsync(ScrapeCategory.New);
        Assert.True(guard.IsCountDropOverrideEnabled);
        Assert.Equal(enabledAt, guard.CountDropOverrideEnabledAt);
    }
}
