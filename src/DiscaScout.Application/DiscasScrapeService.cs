using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// 通常のDISCAS取得をカテゴリ単位で実行し、完全なスナップショットだけを永続化する
/// </summary>
public sealed class DiscasScrapeService(
    IDiscasCategoryCrawler crawler,
    IDiscasSnapshotStore snapshotStore)
{
    private static readonly DiscSourceCategory[] DefaultCategories =
    [
        DiscSourceCategory.Upcoming,
        DiscSourceCategory.New
    ];

    /// <summary>
    /// 通常対象の近日リリース・新作を順番に取得して永続化する
    /// </summary>
    public async Task<ScrapeExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<CategoryScrapeResult>(DefaultCategories.Length);

        foreach (var category in DefaultCategories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteCategoryAsync(category, cancellationToken));
        }

        return new ScrapeExecutionResult(results);
    }

    /// <summary>
    /// 指定した1カテゴリだけを取得して永続化する
    /// </summary>
    public async Task<CategoryScrapeResult> ExecuteCategoryAsync(
        DiscSourceCategory category,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 画像取得は専用BackgroundServiceへ分離しているため、検索結果の完全スナップショットを
            // SQLiteへ反映した時点でカテゴリ取得は完了とする。
            var snapshot = await crawler.CrawlAsync(
                category,
                onPageFetched: null,
                cancellationToken);

            var applyResult = await snapshotStore.ApplyAsync(snapshot, cancellationToken);

            return CategoryScrapeResult.Success(
                category,
                snapshot.TotalCount,
                snapshot.PageCount,
                applyResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CategoryScrapeResult.Failure(category, ex.Message);
        }
    }
}

/// <summary>
/// 通常スクレイピング1回分のカテゴリ別結果を保持する
/// </summary>
public sealed record ScrapeExecutionResult(IReadOnlyList<CategoryScrapeResult> Categories)
{
    public bool IsSuccess => Categories.All(x => x.IsSuccess);
}

/// <summary>
/// 1カテゴリのクロールと永続化結果を保持する
/// </summary>
public sealed record CategoryScrapeResult
{
    private CategoryScrapeResult() { }

    public required DiscSourceCategory Category { get; init; }
    public required bool IsSuccess { get; init; }
    public int? TotalCount { get; init; }
    public int? PageCount { get; init; }
    public int AddedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int DeactivatedSourceCount { get; init; }
    public string? ErrorMessage { get; init; }

    internal static CategoryScrapeResult Success(
        DiscSourceCategory category,
        int totalCount,
        int pageCount,
        SnapshotApplyResult applyResult)
    {
        return new CategoryScrapeResult
        {
            Category = category,
            IsSuccess = true,
            TotalCount = totalCount,
            PageCount = pageCount,
            AddedCount = applyResult.AddedCount,
            UpdatedCount = applyResult.UpdatedCount,
            DeactivatedSourceCount = applyResult.DeactivatedSourceCount
        };
    }

    internal static CategoryScrapeResult Failure(DiscSourceCategory category, string message)
    {
        return new CategoryScrapeResult
        {
            Category = category,
            IsSuccess = false,
            ErrorMessage = message
        };
    }
}
