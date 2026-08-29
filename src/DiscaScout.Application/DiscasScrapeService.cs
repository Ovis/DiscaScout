using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// 通常のDISCAS取得をカテゴリ単位で実行し、完全性と件数安全性を満たすスナップショットだけを永続化する
/// </summary>
public sealed class DiscasScrapeService(
    IDiscasCategoryCrawler crawler,
    IDiscasSnapshotStore snapshotStore,
    IScrapeOperationsStore operationsStore,
    IScrapeGuardStore guardStore)
{
    private const int MinimumAcceptedPercent = 70;
    private static readonly DiscSourceCategory[] DefaultCategories = [DiscSourceCategory.Upcoming, DiscSourceCategory.New];

    /// <summary>通常対象の近日リリース・新作を順番に取得して永続化する</summary>
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

    /// <summary>指定した1カテゴリだけを取得し、件数安全性を確認してから永続化する</summary>
    public async Task<CategoryScrapeResult> ExecuteCategoryAsync(DiscSourceCategory category, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await crawler.CrawlAsync(category, onPageFetched: null, cancellationToken);
            var scrapeCategory = MapCategory(category);
            var previousAccepted = await operationsStore.GetLastAcceptedRunAsync(scrapeCategory, cancellationToken);

            if (snapshot.TotalCount == 0)
            {
                // 初回取得でも0件だけは正常値として採用しない。
                // DISCAS側障害やHTML構造変更を空スナップショットとして反映すると、既存Sourceを誤って消失扱いにするためである。
                return CategoryScrapeResult.AbnormalCountFailure(
                    category,
                    AbnormalCountReason.ZeroCount,
                    snapshot.TotalCount,
                    snapshot.PageCount,
                    previousAccepted?.FetchedCount,
                    previousAccepted?.PageCount,
                    "取得件数が0件のためDBへの反映を中止した");
            }

            var previousCount = previousAccepted?.FetchedCount;
            var isCountDrop = previousCount.HasValue
                && (long)snapshot.TotalCount * 100 < (long)previousCount.Value * MinimumAcceptedPercent;

            var overrideUsed = false;
            if (isCountDrop)
            {
                var baselineCount = previousCount!.Value;
                var guardSettings = await guardStore.GetAsync(scrapeCategory, cancellationToken);
                if (!guardSettings.IsCountDropOverrideEnabled)
                {
                    var ratio = (double)snapshot.TotalCount / baselineCount * 100;
                    return CategoryScrapeResult.AbnormalCountFailure(
                        category,
                        AbnormalCountReason.CountDrop,
                        snapshot.TotalCount,
                        snapshot.PageCount,
                        baselineCount,
                        previousAccepted?.PageCount,
                        $"前回正常件数 {baselineCount}件 に対して今回 {snapshot.TotalCount}件 ({ratio:F1}%) となり、許容下限 {MinimumAcceptedPercent}% を下回ったためDBへの反映を中止した");
                }

                overrideUsed = true;
            }

            // 急減許可を利用する場合は、スナップショット反映と許可消費を永続化層の同一トランザクションへ含める。
            // 片方だけ成功するとRetryでMissingCountを二重加算する可能性があるため、ここでは別々に保存しない。
            var applyResult = await snapshotStore.ApplyAsync(snapshot, overrideUsed, cancellationToken);

            return CategoryScrapeResult.Success(
                category,
                snapshot.TotalCount,
                snapshot.PageCount,
                applyResult,
                overrideUsed,
                previousAccepted?.FetchedCount,
                previousAccepted?.PageCount);
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

    private static ScrapeCategory MapCategory(DiscSourceCategory category) => category switch
    {
        DiscSourceCategory.Upcoming => ScrapeCategory.Upcoming,
        DiscSourceCategory.New => ScrapeCategory.New,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };
}

/// <summary>通常スクレイピング1回分のカテゴリ別結果を保持する</summary>
public sealed record ScrapeExecutionResult(IReadOnlyList<CategoryScrapeResult> Categories)
{
    public bool IsSuccess => Categories.All(x => x.IsSuccess);
}

/// <summary>1カテゴリのクロール・安全判定・永続化結果を保持する</summary>
public sealed record CategoryScrapeResult
{
    private CategoryScrapeResult() { }

    public required DiscSourceCategory Category { get; init; }
    public required bool IsSuccess { get; init; }
    public ScrapeFailureType FailureType { get; init; }
    public AbnormalCountReason? AbnormalCountReason { get; init; }
    public int? TotalCount { get; init; }
    public int? PageCount { get; init; }
    public int? PreviousAcceptedCount { get; init; }
    public int? PreviousAcceptedPageCount { get; init; }
    public int AddedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int DeactivatedSourceCount { get; init; }
    public bool CountDropOverrideUsed { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>失敗後にCoordinatorが登録した次回Retry予定。最終Retry失敗時はnull</summary>
    public DateTime? NextRetryAt { get; init; }

    internal static CategoryScrapeResult Success(
        DiscSourceCategory category,
        int totalCount,
        int pageCount,
        SnapshotApplyResult applyResult,
        bool countDropOverrideUsed,
        int? previousAcceptedCount,
        int? previousAcceptedPageCount) => new()
    {
        Category = category,
        IsSuccess = true,
        FailureType = ScrapeFailureType.None,
        TotalCount = totalCount,
        PageCount = pageCount,
        PreviousAcceptedCount = previousAcceptedCount,
        PreviousAcceptedPageCount = previousAcceptedPageCount,
        AddedCount = applyResult.AddedCount,
        UpdatedCount = applyResult.UpdatedCount,
        DeactivatedSourceCount = applyResult.DeactivatedSourceCount,
        CountDropOverrideUsed = countDropOverrideUsed
    };

    internal static CategoryScrapeResult Failure(DiscSourceCategory category, string message) => new()
    {
        Category = category,
        IsSuccess = false,
        FailureType = ScrapeFailureType.ProcessingError,
        ErrorMessage = message
    };

    internal static CategoryScrapeResult AbnormalCountFailure(
        DiscSourceCategory category,
        AbnormalCountReason reason,
        int totalCount,
        int pageCount,
        int? previousAcceptedCount,
        int? previousAcceptedPageCount,
        string message) => new()
    {
        Category = category,
        IsSuccess = false,
        FailureType = ScrapeFailureType.AbnormalCount,
        AbnormalCountReason = reason,
        TotalCount = totalCount,
        PageCount = pageCount,
        PreviousAcceptedCount = previousAcceptedCount,
        PreviousAcceptedPageCount = previousAcceptedPageCount,
        ErrorMessage = message
    };
}
