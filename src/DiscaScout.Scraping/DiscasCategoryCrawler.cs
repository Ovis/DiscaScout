using System.Net;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASのリリースカテゴリを全ページ取得し、カテゴリ単位で完全なスナップショットを生成する
/// </summary>
public sealed class DiscasCategoryCrawler
{
    private readonly DiscasPageFetcher pageFetcher;
    private readonly DiscasSearchResultParser parser;

    /// <summary>
    /// カテゴリクロール処理を初期化する
    /// </summary>
    /// <param name="pageFetcher">DISCASページのHTTP取得処理</param>
    /// <param name="parser">検索結果ページの解析処理</param>
    public DiscasCategoryCrawler(DiscasPageFetcher pageFetcher, DiscasSearchResultParser parser)
    {
        this.pageFetcher = pageFetcher;
        this.parser = parser;
    }

    /// <summary>
    /// 指定カテゴリを先頭ページから最終ページまで取得する
    /// </summary>
    /// <param name="category">取得対象カテゴリ</param>
    /// <param name="cancellationToken">取得処理を中断するためのトークン</param>
    /// <returns>カテゴリ全体の商品スナップショット</returns>
    /// <exception cref="DiscasCategoryCrawlException">HTTP取得、解析、件数整合性の検証に失敗した場合</exception>
    public async Task<DiscasCategorySnapshot> CrawlAsync(
        DiscSourceCategory category,
        CancellationToken cancellationToken = default)
    {
        var firstPage = await FetchAndParsePageAsync(category, 1, 0, cancellationToken);
        if (firstPage.Products.Count == 0)
        {
            throw new DiscasCategoryCrawlException(category, "1ページ目の商品件数が0件だった");
        }

        if (firstPage.TotalCount is null)
        {
            throw new DiscasCategoryCrawlException(category, "検索結果全体の件数を取得できなかった");
        }

        ValidatePageIds(category, 1, firstPage);

        var totalCount = firstPage.TotalCount.Value;
        var pageSize = firstPage.Products.Count;
        var pageCount = (int)Math.Ceiling(totalCount / (double)pageSize);
        var products = new List<ScrapedDisc>(totalCount);
        products.AddRange(firstPage.Products);

        for (var pageNumber = 2; pageNumber <= pageCount; pageNumber++)
        {
            var page = await FetchAndParsePageAsync(
                category,
                pageNumber,
                products.Count,
                cancellationToken);

            if (page.TotalCount != totalCount)
            {
                throw new DiscasCategoryCrawlException(
                    category,
                    $"ページ{pageNumber}の総件数が1ページ目と一致しない: first={totalCount}, current={page.TotalCount?.ToString() ?? "null"}");
            }

            ValidatePageIds(category, pageNumber, page);
            products.AddRange(page.Products);
        }

        if (products.Count != totalCount)
        {
            throw new DiscasCategoryCrawlException(
                category,
                $"解析商品件数が検索結果の総件数と一致しない: expected={totalCount}, actual={products.Count}");
        }

        var duplicateIds = products
            .GroupBy(x => x.DiscasId, StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .Take(10)
            .ToArray();

        if (duplicateIds.Length > 0)
        {
            throw new DiscasCategoryCrawlException(
                category,
                $"カテゴリ全体でtitleIDが重複している: {string.Join(", ", duplicateIds)}");
        }

        return new DiscasCategorySnapshot(category, totalCount, pageCount, products);
    }

    private async Task<DiscasSearchPage> FetchAndParsePageAsync(
        DiscSourceCategory category,
        int pageNumber,
        int sourceRankOffset,
        CancellationToken cancellationToken)
    {
        var uri = DiscasSearchTarget.CreateUri(category, pageNumber);
        var fetchResult = await pageFetcher.FetchAsync(uri, cancellationToken);

        if (fetchResult.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            throw new DiscasCategoryCrawlException(
                category,
                $"ページ{pageNumber}の取得に失敗した: HTTP {(int)fetchResult.StatusCode} {fetchResult.StatusCode}");
        }

        try
        {
            return parser.Parse(fetchResult.Html, fetchResult.FinalUri, category, sourceRankOffset);
        }
        catch (DiscasSearchParseException ex)
        {
            throw new DiscasCategoryCrawlException(category, $"ページ{pageNumber}の解析に失敗した: {ex.Message}", ex);
        }
    }

    private static void ValidatePageIds(
        DiscSourceCategory category,
        int pageNumber,
        DiscasSearchPage page)
    {
        if (page.Products.Count == 0)
        {
            throw new DiscasCategoryCrawlException(category, $"ページ{pageNumber}の商品件数が0件だった");
        }

        if (page.HiddenTitleIds.Count == 0)
        {
            throw new DiscasCategoryCrawlException(category, $"ページ{pageNumber}のhidden titleIdを取得できなかった");
        }

        var parsedIds = page.Products.Select(x => x.DiscasId).ToArray();

        // hidden titleIdはDISCAS自身がページ内商品として保持しているID一覧なので、
        // DOM selectorの変更で一部商品だけ取りこぼした場合を早期に検出するため完全一致を要求する。
        if (!parsedIds.SequenceEqual(page.HiddenTitleIds, StringComparer.Ordinal))
        {
            throw new DiscasCategoryCrawlException(
                category,
                $"ページ{pageNumber}の商品titleIDとhidden titleIdが一致しない");
        }
    }
}

/// <summary>
/// 1回の正常なカテゴリクロールで得られた完全な商品一覧を保持する
/// </summary>
/// <param name="Category">取得対象カテゴリ</param>
/// <param name="TotalCount">DISCASが示した検索結果総件数</param>
/// <param name="PageCount">取得したページ数</param>
/// <param name="Products">カテゴリ全体の商品一覧</param>
public sealed record DiscasCategorySnapshot(
    DiscSourceCategory Category,
    int TotalCount,
    int PageCount,
    IReadOnlyList<ScrapedDisc> Products);

/// <summary>
/// DISCASカテゴリ全体を安全に取得できず、スナップショットとして利用できない場合に発生する例外
/// </summary>
public sealed class DiscasCategoryCrawlException : Exception
{
    /// <summary>
    /// クロール失敗理由を指定して例外を初期化する
    /// </summary>
    /// <param name="category">失敗したカテゴリ</param>
    /// <param name="message">失敗理由</param>
    public DiscasCategoryCrawlException(DiscSourceCategory category, string message)
        : base(message)
    {
        Category = category;
    }

    /// <summary>
    /// クロール失敗理由と内部例外を指定して例外を初期化する
    /// </summary>
    /// <param name="category">失敗したカテゴリ</param>
    /// <param name="message">失敗理由</param>
    /// <param name="innerException">直接の原因となった例外</param>
    public DiscasCategoryCrawlException(DiscSourceCategory category, string message, Exception innerException)
        : base(message, innerException)
    {
        Category = category;
    }

    /// <summary>
    /// 失敗したリリースカテゴリ
    /// </summary>
    public DiscSourceCategory Category { get; }
}
