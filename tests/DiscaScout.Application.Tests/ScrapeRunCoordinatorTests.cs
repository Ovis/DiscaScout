using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application.Tests;

/// <summary>
/// 実行履歴記録と段階的リトライの状態遷移を検証する
/// </summary>
public sealed class ScrapeRunCoordinatorTests
{
    [Fact]
    public async Task ExecuteAsync_失敗カテゴリは履歴を保存して3時間後のRetryを作る()
    {
        var crawler = new StubCrawler();
        crawler.AddFailure(DiscSourceCategory.Upcoming, "取得失敗");
        crawler.AddSuccess(DiscSourceCategory.New);
        var operations = new StubOperationsStore();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero));
        var coordinator = CreateCoordinator(crawler, operations, clock);

        var result = await coordinator.ExecuteAsync(ScrapeExecutionType.Scheduled);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, operations.Runs.Count);
        var failedRun = Assert.Single(operations.Runs.Where(x => !x.IsSuccess));
        Assert.Equal(ScrapeCategory.Upcoming, failedRun.Category);
        Assert.Equal("取得失敗", failedRun.FailureReason);
        var retry = Assert.Single(operations.Retries.Where(x => x.Status == ScrapeRetryStatus.Pending));
        Assert.Equal(1, retry.AttemptNumber);
        Assert.Equal(clock.GetUtcNow().AddHours(3), retry.DueAt);
    }

    [Fact]
    public async Task ExecuteAsync_成功カテゴリの既存PendingRetryをキャンセルする()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming);
        crawler.AddSuccess(DiscSourceCategory.New);
        var operations = new StubOperationsStore();
        operations.Retries.Add(CreateRetry(10, ScrapeCategory.Upcoming, 1));
        var coordinator = CreateCoordinator(crawler, operations, new TestTimeProvider(DateTimeOffset.UtcNow));

        await coordinator.ExecuteAsync(ScrapeExecutionType.Manual);

        Assert.Equal(ScrapeRetryStatus.Cancelled, operations.Retries.Single(x => x.Id == 10).Status);
    }

    [Fact]
    public async Task ExecuteRetryAsync_1回目失敗時は予定を消費して翌日の最終Retryを作る()
    {
        var crawler = new StubCrawler();
        crawler.AddFailure(DiscSourceCategory.New, "まだ失敗");
        var operations = new StubOperationsStore();
        var firstRetry = CreateRetry(20, ScrapeCategory.New, 1);
        operations.Retries.Add(firstRetry);
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var coordinator = CreateCoordinator(crawler, operations, clock);

        var result = await coordinator.ExecuteRetryAsync(firstRetry);

        Assert.False(result.IsSuccess);
        Assert.Equal(ScrapeRetryStatus.Completed, firstRetry.Status);
        var next = Assert.Single(operations.Retries.Where(x => x.Status == ScrapeRetryStatus.Pending));
        Assert.Equal(2, next.AttemptNumber);
        Assert.Equal(clock.GetUtcNow().AddDays(1), next.DueAt);
    }

    [Fact]
    public async Task ExecuteRetryAsync_2回目失敗後は追加Retryを作らない()
    {
        var crawler = new StubCrawler();
        crawler.AddFailure(DiscSourceCategory.New, "最終失敗");
        var operations = new StubOperationsStore();
        var retry = CreateRetry(30, ScrapeCategory.New, 2);
        operations.Retries.Add(retry);
        var coordinator = CreateCoordinator(crawler, operations, new TestTimeProvider(DateTimeOffset.UtcNow));

        await coordinator.ExecuteRetryAsync(retry);

        Assert.Equal(ScrapeRetryStatus.Completed, retry.Status);
        Assert.Empty(operations.Retries.Where(x => x.Status == ScrapeRetryStatus.Pending));
    }

    [Fact]
    public async Task ExecuteRetryAsync_成功時は同カテゴリの他のPendingRetryをキャンセルする()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming);
        var operations = new StubOperationsStore();
        var current = CreateRetry(40, ScrapeCategory.Upcoming, 1);
        var duplicate = CreateRetry(41, ScrapeCategory.Upcoming, 1);
        operations.Retries.Add(current);
        operations.Retries.Add(duplicate);
        var coordinator = CreateCoordinator(crawler, operations, new TestTimeProvider(DateTimeOffset.UtcNow));

        await coordinator.ExecuteRetryAsync(current);

        Assert.Equal(ScrapeRetryStatus.Completed, current.Status);
        Assert.Equal(ScrapeRetryStatus.Cancelled, duplicate.Status);
    }

    private static ScrapeRunCoordinator CreateCoordinator(
        StubCrawler crawler,
        StubOperationsStore operations,
        TimeProvider clock)
    {
        return new ScrapeRunCoordinator(
            new DiscasScrapeService(crawler, new StubSnapshotStore()),
            operations,
            clock);
    }

    private static ScrapeRetry CreateRetry(long id, ScrapeCategory category, int attemptNumber) => new()
    {
        Id = id,
        Category = category,
        AttemptNumber = attemptNumber,
        DueAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        Status = ScrapeRetryStatus.Pending
    };

    private sealed class StubCrawler : IDiscasCategoryCrawler
    {
        private readonly Dictionary<DiscSourceCategory, string> failures = [];
        private readonly HashSet<DiscSourceCategory> successes = [];

        public void AddFailure(DiscSourceCategory category, string message) => failures[category] = message;
        public void AddSuccess(DiscSourceCategory category) => successes.Add(category);

        public Task<DiscasCategorySnapshot> CrawlAsync(
            DiscSourceCategory category,
            Func<DiscasFetchedPage, CancellationToken, ValueTask>? onPageFetched = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failures.TryGetValue(category, out var message))
            {
                return Task.FromException<DiscasCategorySnapshot>(new InvalidOperationException(message));
            }

            if (!successes.Contains(category))
            {
                throw new InvalidOperationException($"テスト結果が未設定: {category}");
            }

            var disc = new ScrapedDisc(
                category == DiscSourceCategory.New ? "2001" : "1001",
                "https://example.test/disc",
                "作品",
                "アーティスト",
                "J-POP",
                "J-POP",
                null,
                null,
                null,
                category,
                1);
            return Task.FromResult(new DiscasCategorySnapshot(category, 1, 1, [disc]));
        }
    }

    private sealed class StubSnapshotStore : IDiscasSnapshotStore
    {
        public Task<SnapshotApplyResult> ApplyAsync(DiscasCategorySnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.FromResult(new SnapshotApplyResult(1, 0, 0));
    }

    private sealed class StubOperationsStore : IScrapeOperationsStore
    {
        public List<ScrapeRun> Runs { get; } = [];
        public List<ScrapeRetry> Retries { get; } = [];
        private long nextId = 100;

        public Task AddRunAsync(ScrapeRun run, CancellationToken cancellationToken = default)
        {
            Runs.Add(run);
            return Task.CompletedTask;
        }

        public Task EnsureRetryAsync(ScrapeCategory category, int attemptNumber, DateTimeOffset dueAt, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (Retries.Any(x => x.Category == category && x.Status == ScrapeRetryStatus.Pending))
            {
                return Task.CompletedTask;
            }

            Retries.Add(new ScrapeRetry
            {
                Id = nextId++,
                Category = category,
                AttemptNumber = attemptNumber,
                DueAt = dueAt,
                CreatedAt = now,
                Status = ScrapeRetryStatus.Pending
            });
            return Task.CompletedTask;
        }

        public Task CancelPendingRetriesAsync(ScrapeCategory category, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
        {
            foreach (var retry in Retries.Where(x => x.Category == category && x.Status == ScrapeRetryStatus.Pending))
            {
                retry.Status = ScrapeRetryStatus.Cancelled;
                retry.ResolvedAt = resolvedAt;
            }
            return Task.CompletedTask;
        }

        public Task CompleteRetryAsync(long retryId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
        {
            var retry = Retries.Single(x => x.Id == retryId);
            retry.Status = ScrapeRetryStatus.Completed;
            retry.ResolvedAt = resolvedAt;
            return Task.CompletedTask;
        }

        public Task<ScrapeRetry?> GetNextDueRetryAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
            => Task.FromResult(Retries.Where(x => x.Status == ScrapeRetryStatus.Pending && x.DueAt <= now).OrderBy(x => x.DueAt).FirstOrDefault());
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
