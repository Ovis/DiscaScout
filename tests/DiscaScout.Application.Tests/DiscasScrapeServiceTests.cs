using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application.Tests;

/// <summary>
/// 通常スクレイピング実行フローのカテゴリ分離と永続化条件を検証する
/// </summary>
public sealed class DiscasScrapeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_両カテゴリ成功時は順番に取得して保存する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming, CreateSnapshot(DiscSourceCategory.Upcoming, "1001"));
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, "2001"));
        var store = new StubSnapshotStore();
        var service = new DiscasScrapeService(crawler, store);

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
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, "2001"));
        var store = new StubSnapshotStore();
        var service = new DiscasScrapeService(crawler, store);

        var result = await service.ExecuteAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(2, result.Categories.Count);
        Assert.False(result.Categories[0].IsSuccess);
        Assert.Equal("取得失敗", result.Categories[0].ErrorMessage);
        Assert.True(result.Categories[1].IsSuccess);
        Assert.Equal([DiscSourceCategory.New], store.AppliedCategories);
    }

    [Fact]
    public async Task ExecuteAsync_永続化失敗時も次カテゴリを実行する()
    {
        var crawler = new StubCrawler();
        crawler.AddSuccess(DiscSourceCategory.Upcoming, CreateSnapshot(DiscSourceCategory.Upcoming, "1001"));
        crawler.AddSuccess(DiscSourceCategory.New, CreateSnapshot(DiscSourceCategory.New, "2001"));
        var store = new StubSnapshotStore();
        store.Failures[DiscSourceCategory.Upcoming] = new InvalidOperationException("DB反映失敗");
        var service = new DiscasScrapeService(crawler, store);

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
        var service = new DiscasScrapeService(new StubCrawler(), new StubSnapshotStore());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ExecuteAsync(cancellationTokenSource.Token));
    }

    private static DiscasCategorySnapshot CreateSnapshot(DiscSourceCategory category, string id)
    {
        var disc = new ScrapedDisc(
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

        return new DiscasCategorySnapshot(category, 1, 1, [disc]);
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
}
