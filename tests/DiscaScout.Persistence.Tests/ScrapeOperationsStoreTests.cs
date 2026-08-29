using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// SQLiteでUTC DateTimeを扱う運用ストアの比較・並び替えと件数安全装置用照会を検証する
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

    [Fact]
    public async Task GetLastAcceptedRunAsync_異常Runを無視して最後の正常反映を返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc);
        var accepted = CreateRun(ScrapeCategory.New, now.AddHours(-2));
        accepted.FetchedCount = 1000;
        accepted.ParsedCount = 1000;
        accepted.PageCount = 25;
        var anomaly = CreateRun(ScrapeCategory.New, now.AddHours(-1));
        anomaly.IsSuccess = false;
        anomaly.FetchedCount = 600;
        anomaly.ParsedCount = 600;
        anomaly.PageCount = 15;
        anomaly.FailureType = ScrapeFailureType.AbnormalCount;
        anomaly.AbnormalCountReason = AbnormalCountReason.CountDrop;
        database.Context.ScrapeRuns.AddRange(accepted, anomaly);
        await database.Context.SaveChangesAsync();

        var store = new ScrapeOperationsStore(database.Context);
        var baseline = await store.GetLastAcceptedRunAsync(ScrapeCategory.New);

        Assert.NotNull(baseline);
        Assert.Equal(1000, baseline.FetchedCount);
        Assert.Equal(25, baseline.PageCount);
    }

    [Fact]
    public async Task GetLatestAbnormalCountRunAsync_直近の件数異常だけを返す()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = new DateTime(2026, 8, 30, 1, 0, 0, DateTimeKind.Utc);
        var older = CreateRun(ScrapeCategory.Upcoming, now.AddHours(-2));
        older.IsSuccess = false;
        older.FailureType = ScrapeFailureType.AbnormalCount;
        older.AbnormalCountReason = AbnormalCountReason.CountDrop;
        older.FetchedCount = 500;
        var latest = CreateRun(ScrapeCategory.Upcoming, now.AddHours(-1));
        latest.IsSuccess = false;
        latest.FailureType = ScrapeFailureType.AbnormalCount;
        latest.AbnormalCountReason = AbnormalCountReason.ZeroCount;
        latest.FetchedCount = 0;
        database.Context.ScrapeRuns.AddRange(older, latest);
        await database.Context.SaveChangesAsync();

        var store = new ScrapeOperationsStore(database.Context);
        var result = await store.GetLatestAbnormalCountRunAsync(ScrapeCategory.Upcoming);

        Assert.NotNull(result);
        Assert.Equal(0, result.FetchedCount);
        Assert.Equal(AbnormalCountReason.ZeroCount, result.AbnormalCountReason);
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
