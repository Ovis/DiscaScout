using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// カテゴリ別の急減許可がSQLiteへ永続化され、明示的に消費・取消できることを検証する
/// </summary>
public sealed class ScrapeGuardStoreTests
{
    [Fact]
    public async Task GetAsync_未作成カテゴリは無効状態を返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ScrapeGuardStore(database.Context);

        var settings = await store.GetAsync(ScrapeCategory.New);

        Assert.Equal(ScrapeCategory.New, settings.Category);
        Assert.False(settings.IsCountDropOverrideEnabled);
        Assert.Null(settings.CountDropOverrideEnabledAt);
    }

    [Fact]
    public async Task EnableCountDropOverrideAsync_再読み込み後も許可状態を保持する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ScrapeGuardStore(database.Context);
        var enabledAt = new DateTime(2026, 8, 30, 3, 0, 0, DateTimeKind.Utc);

        await store.EnableCountDropOverrideAsync(ScrapeCategory.Upcoming, enabledAt);
        database.Context.ChangeTracker.Clear();
        var settings = await store.GetAsync(ScrapeCategory.Upcoming);

        Assert.True(settings.IsCountDropOverrideEnabled);
        Assert.Equal(enabledAt, settings.CountDropOverrideEnabledAt);
    }

    [Fact]
    public async Task ConsumeCountDropOverrideAsync_許可状態と日時をクリアする()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new ScrapeGuardStore(database.Context);
        await store.EnableCountDropOverrideAsync(ScrapeCategory.New, DateTime.UtcNow);

        await store.ConsumeCountDropOverrideAsync(ScrapeCategory.New);
        database.Context.ChangeTracker.Clear();
        var settings = await store.GetAsync(ScrapeCategory.New);

        Assert.False(settings.IsCountDropOverrideEnabled);
        Assert.Null(settings.CountDropOverrideEnabledAt);
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
