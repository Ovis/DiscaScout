using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application.Tests;

/// <summary>
/// 通常スクレイピング実行フローのカテゴリ分離と件数安全装置を検証する
/// </summary>
public sealed class DiscasScrapeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_両カテゴリ成功時は順番に取得して保存する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming, CreateSnapshot(DiscSourceCategory.Upcoming, 1));
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 1));
        var store = new StubSnapshotStore();
        var service = CreateService(crawler, store);

        var result = await service.ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal([DiscSourceCategory.Upcoming, DiscSourceCategory.New], crawler.RequestedCategories);
        Assert.Equal([DiscSourceCategory.Upcoming, DiscSourceCategory.New], store.AppliedCategories);
        Assert.All(result.Categories, x => Assert.True(x.IsSuccess));
    }

    [Fact]
    public async Task ExecuteAsync_片方のクロール失敗時も他方を実行し失敗カテゴリは保存しない()
    {
        var crawler = new StubCrawler();
        crawler.AddFailure(DiscSourceCategory.Upcoming, new DiscasCategoryCrawlException(DiscSourceCategory.Upcoming, "取得失敗"));
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 1));
        var store = new StubSnapshotStore();
        var service = CreateService(crawler, store);

        var result = await service.ExecuteAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Categories.Count);
        Assert.False(result.Categories[0].IsSuccess);
        Assert.Equal(ScrapeFailureType.ProcessingError, result.Categories[0].FailureType);
        Assert.Equal("取得失敗", result.Categories[0].ErrorMessage);
        Assert.True(result.Categories[1].IsSuccess);
        Assert.Equal([DiscSourceCategory.New], store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteAsync_永続化失敗時も次カテゴリを実行する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming, CreateSnapshot(DiscSourceCategory.Upcoming, 1));
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 1));
        var store = new StubSnapshotStore();
        store.Failures[DiscSourceCategory.Upcoming] = new InvalidOperationException("DB反映失敗");
        var service = CreateService(crawler, store);

        var result = await service.ExecuteAsync();

        Assert.False(result.Categories[0].IsSuccess);
        Assert.Equal("DB反映失敗", result.Categories[0].ErrorMessage);
        Assert.True(result.Categories[1].IsSuccess);
        Assert.Equal([DiscSourceCategory.Upcoming, DiscSourceCategory.New], store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteAsync_キャンセルは障害結果へ変換せず呼び出し元へ伝える()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var service = CreateService(new StubCrawler(), new StubSnapshotStore());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ExecuteAsync(cancellationTokenSource.Token));
    }

    [Fact]
    public async Task ExecuteCategoryAsync_0件は初回でも異常として反映しない()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 0));
        var store = new StubSnapshotStore();
        var service = CreateService(crawler, store);

        var result = await service.ExecuteCategoryAsync(DiscSourceCategory.New);

        Assert.False(result.IsSuccess);
        Assert.Equal(ScrapeFailureType.AbnormalCount, result.FailureType);
        Assert.Equal(AbnormalCountReason.ZeroCount, result.AbnormalCountReason);
        Assert.Equal(0, result.TotalCount);
        Assert.Empty(store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteCategoryAsync_前回正常値の70パーセント未満は異常として反映しない()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 69));
        var store = new StubSnapshotStore();
        var operations = new StubOperationsStore();
        operations.LastAccepted[ScrapeCategory.New] = CreateAcceptedRun(ScrapeCategory.New, 100, 5);
        var service = CreateService(crawler, store, operations);

        var result = await service.ExecuteCategoryAsync(DiscSourceCategory.New);

        Assert.False(result.IsSuccess);
        Assert.Equal(AbnormalCountReason.CountDrop, result.AbnormalCountReason);
        Assert.Equal(100, result.PreviousAcceptedCount);
        Assert.Equal(69, result.TotalCount);
        Assert.Empty(store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteCategoryAsync_前回正常値の70パーセントちょうどは正常として反映する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 70));
        var store = new StubSnapshotStore();
        var operations = new StubOperationsStore();
        operations.LastAccepted[ScrapeCategory.New] = CreateAcceptedRun(ScrapeCategory.New, 100, 5);
        var service = CreateService(crawler, store, operations);

        var result = await service.ExecuteCategoryAsync(DiscSourceCategory.New);

        Assert.True(result.IsSuccess);
        Assert.False(result.CountDropOverrideUsed);
        Assert.Equal([DiscSourceCategory.New], store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteCategoryAsync_急減許可中は反映成功後にだけ許可を消費する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, 60));
        var store = new StubSnapshotStore();
        var operations = new StubOperationsStore();
        operations.LastAccepted[ScrapeCategory.New] = CreateAcceptedRun(ScrapeCategory.New, 100, 5);
        var guard = new StubScrapeGuardStore();
        guard.Settings[ScrapeCategory.New] = new ScrapeGuardSettings
        {
            Category = ScrapeCategory.New,
            IsCountDropOverrideEnabled = true,
            CountDropOverrideEnabledAt = DateTime.UtcNow
        };
        var service = CreateService(crawler, store, operations, guard);

        var result = await service.ExecuteCategoryAsync(DiscSourceCategory.New);

        Assert.True(result.IsSuccess);
        Assert.True(result.CountDropOverrideUsed);
        Assert.Equal(1, guard.ConsumeCount);
        Assert.False(guard.Settings[ScrapeCategory.New].IsCountDropOverrideEnabled);
    }

    private static DiscasScrapeService CreateService(
        StubCrawler crawler,
        StubSnapshotStore store,
        StubOperationsStore? operations = null,
        StubScrapeGuardStore? guard = null)
    {
        return new DiscasScrapeService(
            crawler,
            store,
            operations ?? new StubOperationsStore(),
            guard ?? new StubScrapeGuardStore());
    }

    private static ScrapeRun CreateAcceptedRun(ScrapeCategory category, int count, int pageCount) => new()
    {
        Category = category,
        IsSuccess = true,
        FetchedCount = count,
        ParsedCount = count,
        PageCount = pageCount,
        StartedAt = DateTime.UtcNow.AddMinutes(-1),
        CompletedAt = DateTime.UtcNow
    };

    private static DiscasCategorySnapshot CreateSnapshot(DiscSourceCategory category, int count)
    {
        var discs = Enumerable.Range(1, count)
            .Select(index =>
            {
                var id = $"{(category == DiscSourceCategory.New ? 2 : 1)}{index:0000}";
                return new ScrapedDisc(
                    id,
                    $"https://example.test/goodsDetail.do?titleID={id}",
                    $"作品{id}",
                    $"アーティスト{id}",
                    "J-POP",
                    "J-POP",
                    null,
                    null,
                    null,
                    category,
                    1);
            })
            .ToArray();

        var pageCount = count == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(count / 40d));
        return new DiscasCategorySnapshot(category, count, pageCount, discs);
    }

    /// <summary>
    /// カテゴリごとに成功または失敗を返すテスト用Crawler
    /// </summary>
    private sealed class StubCrawler : IDiscasCategoryCrawler
    {
        private readonly Dictionary<DiscSourceCategory, DiscasCategorySnapshot> successes = [];
        private readonly Dictionary<DiscSourceCategory, Exception> failures = [];

        public List<DiscSourceCategory> RequestedCategories { get; } = [];

        public void AddSuccess(DiscSourceCategory category, DiscasCategorySnapshot snapshot) => successes[category] = snapshot;

        public void AddFailure(DiscSourceCategory category, Exception exception) => failures[category] = exception;

        public Task<DiscasCategorySnapshot> CrawlAsync(
            DiscSourceCategory category,
            Func<DiscasFetchedPage, CancellationToken, ValueTask>? onPageFetched = null,
            CancellationToken cancellationToken = default)
        {
            RequestedCategories.Add(category);
            cancellationToken.ThrowIfCancellationRequested();

            if (failures.TryGetValue(category, out var exception))
            {
                return Task.FromException<DiscasCategorySnapshot>(exception);
            }

            return Task.FromResult(successes[category]);
        }
    }

    /// <summary>
    /// 呼び出されたカテゴリを記録するテスト用永続化処理
    /// </summary>
    private sealed class StubSnapshotStore : IDiscasSnapshotStore
    {
        public List<DiscSourceCategory> AppliedCategories { get; } = [];

        public Dictionary<DiscSourceCategory, Exception> Failures { get; } = [];

        public Task<SnapshotApplyResult> ApplyAsync(
            DiscasCategorySnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            AppliedCategories.Add(snapshot.Category);
            cancellationToken.ThrowIfCancellationRequested();

            if (Failures.TryGetValue(snapshot.Category, out var exception))
            {
                return Task.FromException<SnapshotApplyResult>(exception);
            }

            return Task.FromResult(new SnapshotApplyResult(1, 0, 0));
        }
    }

    /// <summary>
    /// 最後に正常反映したRunだけを返すテスト用運用ストア
    /// </summary>
    private sealed class StubOperationsStore : IScrapeOperationsStore
    {
        public Dictionary<ScrapeCategory, ScrapeRun> LastAccepted { get; } = [];

        public Task AddRunAsync(ScrapeRun run, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnsureRetryAsync(ScrapeCategory category, int attemptNumber, DateTime dueAt, DateTime now, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CancelPendingRetriesAsync(ScrapeCategory category, DateTime resolvedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CompleteRetryAsync(long retryId, DateTime resolvedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ScrapeRetry?> GetNextDueRetryAsync(DateTime now, CancellationToken cancellationToken = default) => Task.FromResult<ScrapeRetry?>(null);
        public Task<ScrapeRun?> GetLastAcceptedRunAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
            => Task.FromResult(LastAccepted.GetValueOrDefault(category));
    }

    /// <summary>
    /// 急減許可の状態と消費回数を記録するテスト用ストア
    /// </summary>
    private sealed class StubScrapeGuardStore : IScrapeGuardStore
    {
        public Dictionary<ScrapeCategory, ScrapeGuardSettings> Settings { get; } = [];
        public int ConsumeCount { get; private set; }

        public Task<ScrapeGuardSettings> GetAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
            => Task.FromResult(Settings.GetValueOrDefault(category) ?? new ScrapeGuardSettings { Category = category });

        public Task EnableCountDropOverrideAsync(ScrapeCategory category, DateTime enabledAt, CancellationToken cancellationToken = default)
        {
            Settings[category] = new ScrapeGuardSettings { Category = category, IsCountDropOverrideEnabled = true, CountDropOverrideEnabledAt = enabledAt };
            return Task.CompletedTask;
        }

        public Task CancelCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
        {
            Settings[category] = new ScrapeGuardSettings { Category = category };
            return Task.CompletedTask;
        }

        public Task ConsumeCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
        {
            ConsumeCount++;
            return CancelCountDropOverrideAsync(category, cancellationToken);
        }
    }
}
