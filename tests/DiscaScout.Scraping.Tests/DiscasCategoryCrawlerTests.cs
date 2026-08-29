using System.Net;
using System.Text;
using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// カテゴリ全ページ取得と完全性検証を確認する
/// </summary>
public sealed class DiscasCategoryCrawlerTests
{
    [Fact]
    public async Task CrawlAsync_全ページが正常なら完全なスナップショットを返す()
    {
        var pages = new Dictionary<int, string>
        {
            [1] = CreatePage(5, ("1001", "作品1"), ("1002", "作品2")),
            [2] = CreatePage(5, ("1003", "作品3"), ("1004", "作品4")),
            [3] = CreatePage(5, ("1005", "作品5"))
        };

        using var httpClient = new HttpClient(new PageHandler(pages));
        var crawler = new DiscasCategoryCrawler(
            new DiscasPageFetcher(httpClient),
            new DiscasSearchResultParser());

        var result = await crawler.CrawlAsync(DiscSourceCategory.New);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(["1001", "1002", "1003", "1004", "1005"], result.Products.Select(x => x.DiscasId));
        Assert.Equal([1, 2, 3, 4, 5], result.Products.Select(x => x.SourceRank));
    }

    [Fact]
    public async Task CrawlAsync_hiddenIdと商品が一致しない場合は失敗する()
    {
        var pages = new Dictionary<int, string>
        {
            [1] = CreatePageWithHiddenIds(2, ["1001", "9999"], ("1001", "作品1"), ("1002", "作品2"))
        };

        using var httpClient = new HttpClient(new PageHandler(pages));
        var crawler = new DiscasCategoryCrawler(
            new DiscasPageFetcher(httpClient),
            new DiscasSearchResultParser());

        var exception = await Assert.ThrowsAsync<DiscasCategoryCrawlException>(
            () => crawler.CrawlAsync(DiscSourceCategory.New));

        Assert.Contains("hidden titleId", exception.Message);
    }

    [Fact]
    public async Task CrawlAsync_最終件数が総件数と一致しない場合は失敗する()
    {
        var pages = new Dictionary<int, string>
        {
            [1] = CreatePage(5, ("1001", "作品1"), ("1002", "作品2")),
            [2] = CreatePage(5, ("1003", "作品3"), ("1004", "作品4")),
            [3] = CreatePageWithHiddenIds(5, ["1005", "1006"], ("1005", "作品5"), ("1006", "作品6"))
        };

        using var httpClient = new HttpClient(new PageHandler(pages));
        var crawler = new DiscasCategoryCrawler(
            new DiscasPageFetcher(httpClient),
            new DiscasSearchResultParser());

        var exception = await Assert.ThrowsAsync<DiscasCategoryCrawlException>(
            () => crawler.CrawlAsync(DiscSourceCategory.New));

        Assert.Contains("総件数と一致しない", exception.Message);
    }

    private static string CreatePage(int totalCount, params (string Id, string Title)[] products)
    {
        return CreatePageWithHiddenIds(totalCount, products.Select(x => x.Id).ToArray(), products);
    }

    private static string CreatePageWithHiddenIds(
        int totalCount,
        IReadOnlyList<string> hiddenIds,
        params (string Id, string Title)[] products)
    {
        var productHtml = string.Join(
            Environment.NewLine,
            products.Select(x => $"""
                <div class="cd-product-item">
                  <div class="card-body-searchCd">
                    <h3><a class="card-title-searchCd" href="goodsDetail.do?titleID={x.Id}">{x.Title}</a></h3>
                    <h3><a href="artistsearchHmo.do?a=1">アーティスト{x.Id}</a></h3>
                  </div>
                  <img class="card-img" src="https://img.discas.net/img/jacket/{x.Id}.jpg">
                </div>
                """));

        return $"""
            <html><body>
              <div class="pagination-cd-search"><p>1～40件 / 全{totalCount}件</p></div>
              <input type="hidden" name="titleId" value="{string.Join(',', hiddenIds)}">
              {productHtml}
            </body></html>
            """;
    }

    /// <summary>
    /// ページ番号に対応した固定HTMLを返すテスト用HTTPハンドラー
    /// </summary>
    private sealed class PageHandler(IReadOnlyDictionary<int, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pageNumber = ParsePageNumber(request.RequestUri!);
            if (!pages.TryGetValue(pageNumber, out var html))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html")
            {
                CharSet = "utf-8"
            };
            return Task.FromResult(response);
        }

        private static int ParsePageNumber(Uri uri)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("PN", StringComparison.OrdinalIgnoreCase))
                {
                    return int.Parse(parts[1]);
                }
            }

            return 1;
        }
    }
}
