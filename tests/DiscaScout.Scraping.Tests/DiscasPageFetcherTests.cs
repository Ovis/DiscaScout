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
    [Fact]
    public async Task FetchAsync_Windows31JResponse_DecodesAsCodePage932()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        const string expectedHtml = "<html><body>新作CD</body></html>";
        var content = new ByteArrayContent(Encoding.GetEncoding(932).GetBytes(expectedHtml));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "Windows-31J" };

        using var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var httpClient = new HttpClient(handler);
        var fetcher = new DiscasPageFetcher(httpClient, TimeSpan.Zero);

        var result = await fetcher.FetchAsync(new Uri("https://example.test/search"));

        Assert.Equal("Windows-31J", result.Charset);
        Assert.Equal(expectedHtml, result.Html);
    }

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
        var starts = handler.StartedAt.Order().ToArray();
        Assert.Equal(2, starts.Length);
        Assert.True(starts[1] - starts[0] >= TimeSpan.FromMilliseconds(80));
    }

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

    [Fact]
    public async Task AcquireAsync_BurstSize到達後は次要求前に追加休止する()
    {
        var pauseCount = 0;
        var throttle = new DiscasRequestThrottle(
            TimeSpan.Zero,
            burstSize: 2,
            () =>
            {
                pauseCount++;
                return TimeSpan.FromMilliseconds(80);
            });

        using (await throttle.AcquireAsync()) { }
        using (await throttle.AcquireAsync()) { }
        var startedAt = DateTimeOffset.UtcNow;
        using (await throttle.AcquireAsync()) { }

        Assert.Equal(1, pauseCount);
        Assert.True(DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMilliseconds(60));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private int activeRequests;
        private int maximumConcurrentRequests;
        internal ConcurrentBag<DateTimeOffset> StartedAt { get; } = [];
        internal int MaximumConcurrentRequests => maximumConcurrentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref activeRequests);
            UpdateMaximum(current);
            StartedAt.Add(DateTimeOffset.UtcNow);

            try
            {
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
