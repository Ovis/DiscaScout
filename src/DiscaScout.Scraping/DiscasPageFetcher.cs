using System.Net;

namespace DiscaScout.Scraping;

public sealed class DiscasPageFetcher(HttpClient httpClient)
{
    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; DiscaScout/0.1; +https://github.com/Ovis/DiscaScout)");
        request.Headers.AcceptLanguage.ParseAdd("ja-JP,ja;q=0.9,en;q=0.5");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        return new FetchResult(
            response.StatusCode,
            response.RequestMessage?.RequestUri ?? uri,
            response.Content.Headers.ContentType?.CharSet,
            html);
    }
}

public sealed record FetchResult(
    HttpStatusCode StatusCode,
    Uri FinalUri,
    string? Charset,
    string Html);
