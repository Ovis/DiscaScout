using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// 急減許可を利用したスナップショット反映と許可消費が同一トランザクションになることを検証する
/// </summary>
public sealed class DiscasSnapshotOverrideTests
{
    [Fact]
    public async Task ApplyAsync_急減許可を使う場合はスナップショット反映と同時に許可を消費する()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ScrapeGuardSettings.Add(new ScrapeGuardSettings
        {
            Category = ScrapeCategory.New,
            IsCountDropOverrideEnabled = true,
            CountDropOverrideEnabledAt = DateTime.UtcNow
        });
        await database.Context.SaveChangesAsync();
        var applier = new DiscasSnapshotApplier(database.Context);

        await applier.ApplyAsync(CreateSnapshot(), consumeCountDropOverride: true);

        database.Context.ChangeTracker.Clear();
        var guard = await database.Context.ScrapeGuardSettings.AsNoTracking().SingleAsync();
        Assert.False(guard.IsCountDropOverrideEnabled);
        Assert.Null(guard.CountDropOverrideEnabledAt);
        Assert.Equal(1, await database.Context.Discs.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_許可状態が無効ならスナップショット側もロールバックする()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ScrapeGuardSettings.Add(new ScrapeGuardSettings
        {
            Category = ScrapeCategory.New,
            IsCountDropOverrideEnabled = false
        });
        await database.Context.SaveChangesAsync();
        var applier = new DiscasSnapshotApplier(database.Context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => applier.ApplyAsync(CreateSnapshot(), consumeCountDropOverride: true));

        database.Context.ChangeTracker.Clear();
        Assert.Equal(0, await database.Context.Discs.CountAsync());
    }

    private static DiscasCategorySnapshot CreateSnapshot()
    {
        var disc = new ScrapedDisc(
            "1001",
            "https://example.test/goodsDetail.do?titleID=1001",
            "作品1",
            "アーティスト1",
            "J-POP",
            "J-POP",
            null,
            null,
            null,
            DiscSourceCategory.New,
            1);
        return new DiscasCategorySnapshot(DiscSourceCategory.New, 1, 1, [disc]);
    }

    /// <summary>SQLite実プロバイダーをメモリ上で維持するテスト用DBを管理する</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, DiscaScoutDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public DiscaScoutDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DiscaScoutDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new DiscaScoutDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
