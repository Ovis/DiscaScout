using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// 手動バックグラウンド処理キューの重複防止・順序・再起動復旧をSQLite実プロバイダーで検証する
/// </summary>
public sealed class ManualWorkStoreTests
{
    [Fact]
    public async Task TryEnqueueFullScrapeAsync_保留中の通常取得がある場合は重複登録しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

        var first = await store.TryEnqueueFullScrapeAsync(now);
        var second = await store.TryEnqueueFullScrapeAsync(now.AddMinutes(1));

        Assert.True(first);
        Assert.False(second);
        Assert.Single(database.Context.ManualWorkItems);
    }

    [Fact]
    public async Task TryEnqueueCategoryScrapeAsync_同カテゴリは重複防止し別カテゴリは登録できる()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(await store.TryEnqueueCategoryScrapeAsync(ScrapeCategory.New, now));
        Assert.False(await store.TryEnqueueCategoryScrapeAsync(ScrapeCategory.New, now.AddMinutes(1)));
        Assert.True(await store.TryEnqueueCategoryScrapeAsync(ScrapeCategory.Upcoming, now.AddMinutes(2)));

        var items = await database.Context.ManualWorkItems.AsNoTracking().OrderBy(x => x.RequestedAt).ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.Equal(ScrapeCategory.New, items[0].Category);
        Assert.Equal(ScrapeCategory.Upcoming, items[1].Category);
    }

    [Fact]
    public async Task TryEnqueueCategoryScrapeAsync_FullScrape保留中は追加登録しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(await store.TryEnqueueFullScrapeAsync(now));
        Assert.False(await store.TryEnqueueCategoryScrapeAsync(ScrapeCategory.New, now.AddMinutes(1)));
        Assert.Single(database.Context.ManualWorkItems);
    }

    [Fact]
    public async Task TryEnqueueFullScrapeAsync_CategoryScrape保留中は追加登録しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(await store.TryEnqueueCategoryScrapeAsync(ScrapeCategory.New, now));
        Assert.False(await store.TryEnqueueFullScrapeAsync(now.AddMinutes(1)));
        Assert.Single(database.Context.ManualWorkItems);
    }

    [Fact]
    public async Task TryEnqueueArtistCatalogAsync_同じArtistだけ重複を防ぎ別Artistは登録する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(await store.TryEnqueueArtistCatalogAsync(10, now));
        Assert.False(await store.TryEnqueueArtistCatalogAsync(10, now.AddMinutes(1)));
        Assert.True(await store.TryEnqueueArtistCatalogAsync(20, now.AddMinutes(2)));

        Assert.Equal(2, await database.Context.ManualWorkItems.CountAsync());
    }

    [Fact]
    public async Task GetNextPendingAsync_要求時刻が最も古い処理を返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await store.TryEnqueueArtistCatalogAsync(10, now.AddMinutes(2));
        await store.TryEnqueueFullScrapeAsync(now);

        var item = await store.GetNextPendingAsync();

        Assert.NotNull(item);
        Assert.Equal(ManualWorkType.FullScrape, item.Type);
    }

    [Fact]
    public async Task RecoverInterruptedAsync_RunningをPendingへ戻して再実行可能にする()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await store.TryEnqueueFullScrapeAsync(now);
        var item = await store.GetNextPendingAsync();
        Assert.NotNull(item);
        await store.MarkRunningAsync(item.Id, now.AddMinutes(1));

        await store.RecoverInterruptedAsync();

        var recovered = await database.Context.ManualWorkItems.AsNoTracking().SingleAsync();
        Assert.Equal(ManualWorkStatus.Pending, recovered.Status);
        Assert.Null(recovered.StartedAt);
        Assert.Null(recovered.CompletedAt);
        Assert.Null(recovered.FailureReason);
    }

    [Fact]
    public async Task GetRecentAsync_要求時刻の新しい順に返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ManualWorkStore(database.Context);
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        await store.TryEnqueueArtistCatalogAsync(10, now);
        await store.TryEnqueueArtistCatalogAsync(20, now.AddMinutes(1));

        var recent = await store.GetRecentAsync(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal(20, recent[0].ArtistSettingId);
        Assert.Equal(10, recent[1].ArtistSettingId);
    }

    /// <summary>
    /// SQLite実プロバイダーをメモリ上で維持するテスト用DBを管理する
    /// </summary>
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
