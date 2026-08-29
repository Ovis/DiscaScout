using System.Net;
using System.Text;

namespace DiscaScout.Scraping;

/// <summary>
/// TSUTAYA DISCASのページをHTTPで取得し、レスポンスの文字コードに従ってHTMLを文字列へ変換する
/// </summary>
public sealed class DiscasPageFetcher
{
    private const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    private readonly HttpClient httpClient;

    static DiscasPageFetcher()
    {
        // DISCASはWindows-31Jをcharsetとして返すページがある。
        // .NETでは既定でコードページ系Encodingが有効ではないため、ライブラリ側で登録して
        // 呼び出し元の初期化方法に依存せずレスポンスをデコードできるようにする。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// DISCASページ取得クライアントを初期化する
    /// </summary>
    /// <param name="httpClient">HTTP通信に使用するクライアント</param>
    public DiscasPageFetcher(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    /// <summary>
    /// 指定されたDISCASページを取得する
    /// </summary>
    /// <param name="uri">取得対象の絶対URL</param>
    /// <param name="cancellationToken">取得処理を中断するためのトークン</param>
    /// <returns>HTTPステータス、最終URL、文字コード、HTML本文を含む取得結果</returns>
    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // DISCAS側に通常のデスクトップブラウザからのアクセスと同等のUser-Agentを送る。
        // 独自CrawlerのUser-Agentによって通常ブラウザと異なるレスポンスになる可能性を避けるため固定している。
        request.Headers.UserAgent.ParseAdd(ChromeUserAgent);
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

/// <summary>
/// DISCASページのHTTP取得結果を保持する
/// </summary>
/// <param name="StatusCode">HTTPステータスコード</param>
/// <param name="FinalUri">リダイレクト後の最終URL</param>
/// <param name="Charset">レスポンスで指定された文字コード</param>
/// <param name="Html">デコード済みHTML本文</param>
public sealed record FetchResult(
    HttpStatusCode StatusCode,
    Uri FinalUri,
    string? Charset,
    string Html);
