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
    /// <param name="cancellationToken">実行全体を中断するためのトークン</param>
    /// <returns>カテゴリごとの成功・失敗と反映件数を含む実行結果</returns>
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
    /// <param name="category">取得対象カテゴリ</param>
    /// <param name="cancellationToken">実行を中断するためのトークン</param>
    /// <returns>カテゴリ単位の実行結果</returns>
    public async Task<CategoryScrapeResult> ExecuteCategoryAsync(
        DiscSourceCategory category,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Crawlerはカテゴリ全ページの整合性確認が完了した場合だけSnapshotを返す。
            // そのため永続化はCrawl完了後にのみ行い、途中まで取得できたデータをDBへ反映しない。
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
            // 明示的な停止要求は障害として記録せず、上位のBackgroundService等へそのまま伝える。
            throw;
        }
        catch (Exception ex)
        {
            // NewとUpcomingは独立したコミット単位であるため、片方の失敗で他方まで中止しない。
            // 詳細な例外情報は後続のログ基盤へ流し、実行結果には利用者向けの短い理由だけ保持する。
            return CategoryScrapeResult.Failure(category, ex.Message);
        }
    }
}

/// <summary>
/// 通常スクレイピング1回分のカテゴリ別結果を保持する
/// </summary>
/// <param name="Categories">実行したカテゴリの結果</param>
public sealed record ScrapeExecutionResult(IReadOnlyList<CategoryScrapeResult> Categories)
{
    /// <summary>
    /// 全カテゴリが正常終了したか
    /// </summary>
    public bool IsSuccess => Categories.All(x => x.IsSuccess);
}

/// <summary>
/// 1カテゴリのクロールと永続化結果を保持する
/// </summary>
public sealed record CategoryScrapeResult
{
    private CategoryScrapeResult()
    {
    }

    /// <summary>取得対象カテゴリ</summary>
    public required DiscSourceCategory Category { get; init; }

    /// <summary>クロールと永続化の両方が成功したか</summary>
    public required bool IsSuccess { get; init; }

    /// <summary>DISCASが報告したカテゴリ総件数。取得失敗時はnull</summary>
    public int? TotalCount { get; init; }

    /// <summary>取得したページ数。取得失敗時はnull</summary>
    public int? PageCount { get; init; }

    /// <summary>新規作成したCD数</summary>
    public int AddedCount { get; init; }

    /// <summary>更新した既存CD数</summary>
    public int UpdatedCount { get; init; }

    /// <summary>Inactiveへ移したカテゴリSource数</summary>
    public int DeactivatedSourceCount { get; init; }

    /// <summary>失敗理由。成功時はnull</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 正常終了した結果を生成する
    /// </summary>
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

    /// <summary>
    /// 失敗した結果を生成する
    /// </summary>
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
