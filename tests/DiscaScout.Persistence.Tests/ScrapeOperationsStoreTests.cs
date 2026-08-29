using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// SQLiteでUTC DateTimeを扱う運用ストアの比較・並び替えを検証する
/// </summary>
public sealed class ScrapeOperationsStoreTests
{
    [Fact]
    public async Task GetNextDueRetryAsync_SQLiteで期限到来済みの最古Retryを返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        database.Context.ScrapeRetries.AddRange(
            new ScrapeRetry
            {
                Category = ScrapeCategory.Upcoming,
                AttemptNumber = 1,
                DueAt = now.AddMinutes(-10),
                Status = ScrapeRetryStatus.Pending,
                CreatedAt = now.AddHours(-1)
            },
            new ScrapeRetry
            {
                Category = ScrapeCategory.New,
                AttemptNumber = 1,
                DueAt = now.AddMinutes(-30),
                Status = ScrapeRetryStatus.Pending,
                CreatedAt = now.AddHours(-1)
            });
        await database.Context.SaveChangesAsync();

        var store = new ScrapeOperationsStore(database.Context);
        var retry = await store.GetNextDueRetryAsync(now);

        Assert.NotNull(retry);
        Assert.Equal(ScrapeCategory.New, retry.Category);
    }

    [Fact]
    public async Task GetRecentRunsAsync_SQLiteで開始時刻の新しい順に返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        database.Context.ScrapeRuns.AddRange(
            CreateRun(ScrapeCategory.Upcoming, now.AddHours(-2)),
            CreateRun(ScrapeCategory.New, now.AddHours(-1)));
        await database.Context.SaveChangesAsync();

        var store = new ScrapeOperationsStore(database.Context);
        var runs = await store.GetRecentRunsAsync(2);

        Assert.Equal(2, runs.Count);
        Assert.Equal(ScrapeCategory.New, runs[0].Category);
        Assert.Equal(ScrapeCategory.Upcoming, runs[1].Category);
    }

    private static ScrapeRun CreateRun(ScrapeCategory category, DateTime startedAt)
    {
        return new ScrapeRun
        {
            ExecutionType = ScrapeExecutionType.Manual,
            Category = category,
            StartedAt = startedAt,
            CompletedAt = startedAt.AddMinutes(1),
            DurationMilliseconds = 60_000,
            IsSuccess = true
        };
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
