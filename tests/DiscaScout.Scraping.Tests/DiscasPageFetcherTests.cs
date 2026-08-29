using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCASページ取得時のHTTPレスポンス処理とアクセス間隔制御を検証する
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
        var fetcher = new DiscasPageFetcher(httpClient, TimeSpan.Zero);

        var result = await fetcher.FetchAsync(new Uri("https://example.test/search"));

        Assert.Equal("Windows-31J", result.Charset);
        Assert.Equal(expectedHtml, result.Html);
    }

    /// <summary>
    /// 複数の取得要求が同時に来てもDISCASへのHTTP要求が並列化されず、開始間隔も維持されることを確認する
    /// </summary>
    [Fact]
    public async Task FetchAsync_ConcurrentRequests_AreSerializedAndThrottled()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var fetcher = new DiscasPageFetcher(httpClient, TimeSpan.FromMilliseconds(100));

        await Task.WhenAll(
            fetcher.FetchAsync(new Uri("https://example.test/search?pn=1")),
            fetcher.FetchAsync(new Uri("https://example.test/search?pn=2")));

        Assert.Equal(1, handler.MaximumConcurrentRequests);
        Assert.Equal(2, handler.StartedAt.Count);

        var starts = handler.StartedAt.Order().ToArray();
        Assert.True(
            starts[1] - starts[0] >= TimeSpan.FromMilliseconds(80),
            $"リクエスト開始間隔が短すぎる: {starts[1] - starts[0]}");
    }

    /// <summary>
    /// CategoryとArtistでFetcherが別インスタンスでも共有Throttleにより同時HTTPアクセスしないことを確認する
    /// </summary>
    [Fact]
    public async Task FetchAsync_DifferentFetcherInstances_SharedThrottleSerializesRequests()
    {
        var handler = new RecordingHttpMessageHandler();
        using var httpClient1 = new HttpClient(handler, disposeHandler: false);
        using var httpClient2 = new HttpClient(handler, disposeHandler: false);
        var throttle = new DiscasRequestThrottle(TimeSpan.FromMilliseconds(100));
        var firstFetcher = new DiscasPageFetcher(httpClient1, throttle);
        var secondFetcher = new DiscasPageFetcher(httpClient2, throttle);

        await Task.WhenAll(
            firstFetcher.FetchAsync(new Uri("https://example.test/category")),
            secondFetcher.FetchAsync(new Uri("https://example.test/artist")));

        Assert.Equal(1, handler.MaximumConcurrentRequests);
        var starts = handler.StartedAt.Order().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(starts[1] - starts[0] >= TimeSpan.FromMilliseconds(80));

        handler.Dispose();
    }

    /// <summary>
    /// 外部通信を行わず、指定したHTTPレスポンスを取得クラスへ返すテスト用ハンドラー
    /// </summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// HTTP要求の開始時刻と同時実行数を記録するテスト用ハンドラー
    /// </summary>
    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private int activeRequests;
        private int maximumConcurrentRequests;

        internal ConcurrentBag<DateTimeOffset> StartedAt { get; } = [];

        internal int MaximumConcurrentRequests => maximumConcurrentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref activeRequests);
            UpdateMaximum(current);
            StartedAt.Add(DateTimeOffset.UtcNow);

            try
            {
                // Fetcherがレスポンス完了まで排他を保持していることを検証できるよう、短時間だけ要求を継続させる。
                await Task.Delay(30, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent("<html></html>", Encoding.UTF8, "text/html")
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = maximumConcurrentRequests;
                if (current <= observed || Interlocked.CompareExchange(ref maximumConcurrentRequests, current, observed) == observed)
                {
                    return;
                }
            }
        }
    }
}
