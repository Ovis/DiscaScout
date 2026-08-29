using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCASページ取得時のHTTPレスポンス処理を検証する
/// </summary>
public sealed class DiscasPageFetcherTests
{
    /// <summary>
    /// DISCASがcharsetとしてWindows-31Jを返してもCP932として正常にデコードできることを確認する
    /// </summary>
    [Fact]
    public async Task FetchAsync_Windows31JResponse_DecodesAsCodePage932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        const string expectedHtml = "<html><body>新作CD</body></html>";
        var content = new ByteArrayContent(Encoding.GetEncoding(932).GetBytes(expectedHtml));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html")
        {
            CharSet = "Windows-31J"
        };

        using var handler = new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
        using var httpClient = new HttpClient(handler);
        var fetcher = new DiscasPageFetcher(httpClient);

        var result = await fetcher.FetchAsync(new Uri("https://example.test/search"));

        Assert.Equal("Windows-31J", result.Charset);
        Assert.Equal(expectedHtml, result.Html);
    }

    /// <summary>
    /// 外部通信を行わず、指定したHTTPレスポンスを取得クラスへ返すテスト用ハンドラー
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        /// <summary>
        /// あらかじめ用意したレスポンスを返す
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
