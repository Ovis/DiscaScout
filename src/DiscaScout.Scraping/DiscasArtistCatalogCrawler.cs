using System.Net;
using Microsoft.Extensions.Logging;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASのアーティスト検索結果を全ページ取得する処理の契約
/// </summary>
public interface IDiscasArtistCatalogCrawler
{
    /// <summary>
    /// 指定アーティスト名の検索結果を先頭ページから最終ページまで取得する
    /// </summary>
    /// <param name="artist">DISCASへ送信するアーティスト検索語</param>
    /// <param name="cancellationToken">取得処理を中断するためのトークン</param>
    /// <returns>検索結果全体の商品スナップショット</returns>
    Task<DiscasArtistCatalogSnapshot> CrawlAsync(
        string artist,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// DISCASのアーティスト検索を全ページ取得し、完全な検索結果スナップショットを生成する
/// </summary>
public sealed class DiscasArtistCatalogCrawler(
    DiscasPageFetcher pageFetcher,
    DiscasSearchResultParser parser,
    ILogger<DiscasArtistCatalogCrawler>? logger = null) : IDiscasArtistCatalogCrawler
{
    /// <inheritdoc />
    public async Task<DiscasArtistCatalogSnapshot> CrawlAsync(
        string artist,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        var searchArtist = artist.Trim();
        var searchMode = ArtistSearchMode.Artist;
        var firstFetch = await FetchPageAsync(searchArtist, 1, searchMode, cancellationToken);

        // AK/AKNはDISCAS側で成立しているアーティスト名を指定する検索であり、
        // 部分的・旧称など曖昧な名称では404になる。1ページ目が404の場合だけK検索へ切り替え、
        // 通信障害やサーバー障害まで別検索で隠さないようにする。
        if (firstFetch.StatusCode == HttpStatusCode.NotFound)
        {
            searchMode = ArtistSearchMode.Keyword;
            firstFetch = await FetchPageAsync(searchArtist, 1, searchMode, cancellationToken);
        }

        var firstPage = ParsePageOrThrow(searchArtist, 1, 0, firstFetch);
        if (firstPage.TotalCount is null)
        {
            throw new DiscasArtistCatalogCrawlException(searchArtist, "検索結果全体の件数を取得できなかった");
        }

        var totalCount = firstPage.TotalCount.Value;
        if (totalCount == 0)
        {
            if (firstPage.Products.Count != 0)
            {
                throw new DiscasArtistCatalogCrawlException(searchArtist, "総件数0件だが商品が解析された");
            }

            return new DiscasArtistCatalogSnapshot(searchArtist, 0, 1, []);
        }

        ValidatePageIds(searchArtist, 1, firstPage);
        if (firstPage.Products.Count == 0)
        {
            throw new DiscasArtistCatalogCrawlException(searchArtist, "1ページ目の商品件数が0件だった");
        }

        var pageSize = firstPage.Products.Count;
        var pageCount = (int)Math.Ceiling(totalCount / (double)pageSize);
        var products = new List<ScrapedDisc>(totalCount);
        products.AddRange(firstPage.Products);

        for (var pageNumber = 2; pageNumber <= pageCount; pageNumber++)
        {
            // 1ページ目でK検索へフォールバックした場合は、後続ページも同じ検索方式を維持する。
            var fetch = await FetchPageAsync(searchArtist, pageNumber, searchMode, cancellationToken);
            var page = ParsePageOrThrow(searchArtist, pageNumber, products.Count, fetch);
            if (page.TotalCount != totalCount)
            {
                throw new DiscasArtistCatalogCrawlException(
                    searchArtist,
                    $"ページ{pageNumber}の総件数が1ページ目と一致しない: first={totalCount}, current={page.TotalCount?.ToString() ?? "null"}");
            }

            ValidatePageIds(searchArtist, pageNumber, page);
            products.AddRange(page.Products);
        }

        if (products.Count != totalCount)
        {
            throw new DiscasArtistCatalogCrawlException(
                searchArtist,
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
            throw new DiscasArtistCatalogCrawlException(
                searchArtist,
                $"検索結果全体でtitleIDが重複している: {string.Join(", ", duplicateIds)}");
        }

        return new DiscasArtistCatalogSnapshot(searchArtist, totalCount, pageCount, products);
    }

    private async Task<ArtistSearchFetchResult> FetchPageAsync(
        string artist,
        int pageNumber,
        ArtistSearchMode searchMode,
        CancellationToken cancellationToken)
    {
        var uri = searchMode switch
        {
            ArtistSearchMode.Artist => DiscasSearchTarget.CreateArtistUri(artist, pageNumber),
            ArtistSearchMode.Keyword => DiscasSearchTarget.CreateArtistKeywordUri(artist, pageNumber),
            _ => throw new ArgumentOutOfRangeException(nameof(searchMode))
        };

        // フォールバック後に実際にどのURLへアクセスしているかを確認するための診断ログ。
        // 問題の切り分けが完了したらログレベルや出力有無を改めて見直す。
        logger?.LogInformation(
            "DISCAS Artist Catalog検索を取得します。Mode={SearchMode}, Page={PageNumber}, Url={SearchUrl}",
            searchMode,
            pageNumber,
            uri.OriginalString);

        var fetchResult = await pageFetcher.FetchAsync(uri, cancellationToken);
        return new ArtistSearchFetchResult(uri, fetchResult);
    }

    private DiscasSearchPage ParsePageOrThrow(
        string artist,
        int pageNumber,
        int sourceRankOffset,
        ArtistSearchFetchResult fetch)
    {
        var fetchResult = fetch.Result;
        if (fetchResult.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            // DISCAS側の一時障害、検索URL生成不備、リダイレクト後の404を切り分けられるよう、
            // 失敗時だけ要求URLと最終URLを例外へ含める。
            var redirected = Uri.Compare(
                fetch.RequestUri,
                fetchResult.FinalUri,
                UriComponents.AbsoluteUri,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase) != 0;
            throw new DiscasArtistCatalogCrawlException(
                artist,
                $"ページ{pageNumber}の取得に失敗した: HTTP {(int)fetchResult.StatusCode} {fetchResult.StatusCode}; " +
                $"RequestUri={fetch.RequestUri}; FinalUri={fetchResult.FinalUri}; Redirected={redirected}");
        }

        try
        {
            return parser.Parse(
                fetchResult.Html,
                fetchResult.FinalUri,
                DiscSourceCategory.ArtistCatalog,
                sourceRankOffset);
        }
        catch (DiscasSearchParseException ex)
        {
            throw new DiscasArtistCatalogCrawlException(
                artist,
                $"ページ{pageNumber}の解析に失敗した: {ex.Message}",
                ex);
        }
    }

    private static void ValidatePageIds(string artist, int pageNumber, DiscasSearchPage page)
    {
        if (page.Products.Count == 0)
        {
            throw new DiscasArtistCatalogCrawlException(artist, $"ページ{pageNumber}の商品件数が0件だった");
        }

        if (page.HiddenTitleIds.Count == 0)
        {
            throw new DiscasArtistCatalogCrawlException(artist, $"ページ{pageNumber}のhidden titleIdを取得できなかった");
        }

        var parsedIds = page.Products.Select(x => x.DiscasId).ToArray();
        if (!parsedIds.SequenceEqual(page.HiddenTitleIds, StringComparer.Ordinal))
        {
            throw new DiscasArtistCatalogCrawlException(
                artist,
                $"ページ{pageNumber}の商品titleIDとhidden titleIdが一致しない");
        }
    }

    /// <summary>
    /// Artist Catalogの取得中に維持するDISCAS検索方式
    /// </summary>
    private enum ArtistSearchMode
    {
        Artist,
        Keyword
    }

    /// <summary>
    /// HTTP取得結果と、リダイレクト前の要求URLをまとめて保持する
    /// </summary>
    private sealed record ArtistSearchFetchResult(Uri RequestUri, FetchResult Result)
    {
        public HttpStatusCode StatusCode => Result.StatusCode;
    }
}

/// <summary>
/// 1回の正常なアーティスト検索で得られた完全な商品一覧を保持する
/// </summary>
/// <param name="SearchArtist">DISCASへ送信したアーティスト検索語</param>
/// <param name="TotalCount">検索結果全体の件数</param>
/// <param name="PageCount">取得したページ数</param>
/// <param name="Products">検索結果として取得した全商品</param>
public sealed record DiscasArtistCatalogSnapshot(
    string SearchArtist,
    int TotalCount,
    int PageCount,
    IReadOnlyList<ScrapedDisc> Products);

/// <summary>
/// DISCASのアーティスト検索全体を安全に取得できず、スナップショットとして利用できない場合に発生する例外
/// </summary>
public sealed class DiscasArtistCatalogCrawlException : Exception
{
    /// <summary>取得対象アーティストと失敗理由を指定して初期化する</summary>
    public DiscasArtistCatalogCrawlException(string artist, string message) : base(message) => Artist = artist;

    /// <summary>取得対象アーティスト、失敗理由、内部例外を指定して初期化する</summary>
    public DiscasArtistCatalogCrawlException(string artist, string message, Exception innerException)
        : base(message, innerException) => Artist = artist;

    /// <summary>取得対象のアーティスト検索語</summary>
    public string Artist { get; }
}
