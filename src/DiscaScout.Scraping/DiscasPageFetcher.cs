using System.Net;
using System.Text;

namespace DiscaScout.Scraping;

/// <summary>
/// すべてのDISCAS HTTPアクセスで共有するリクエスト間隔と排他制御を提供する
/// </summary>
public sealed class DiscasRequestThrottle
{
    private static readonly TimeSpan DefaultMinimumRequestInterval = TimeSpan.FromSeconds(2);

    private readonly TimeSpan minimumRequestInterval;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset? lastRequestStartedAt;

    /// <summary>
    /// 本番用の最低2秒間隔でスロットルを初期化する
    /// </summary>
    public DiscasRequestThrottle()
        : this(DefaultMinimumRequestInterval)
    {
    }

    /// <summary>
    /// テスト用にリクエスト開始時刻の最低間隔を指定して初期化する
    /// </summary>
    /// <param name="minimumRequestInterval">リクエスト開始時刻の最低間隔</param>
    internal DiscasRequestThrottle(TimeSpan minimumRequestInterval)
    {
        if (minimumRequestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        }

        this.minimumRequestInterval = minimumRequestInterval;
    }

    /// <summary>
    /// 次のDISCASリクエストを開始できるまで待機し、排他スロットを取得する
    /// </summary>
    /// <param name="cancellationToken">待機を中断するためのトークン</param>
    /// <returns>HTTPレスポンス処理完了時に破棄する排他スロット</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            if (lastRequestStartedAt is not null && minimumRequestInterval > TimeSpan.Zero)
            {
                var elapsed = DateTimeOffset.UtcNow - lastRequestStartedAt.Value;
                var remaining = minimumRequestInterval - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }

            lastRequestStartedAt = DateTimeOffset.UtcNow;
            return new Releaser(requestGate);
        }
        catch
        {
            requestGate.Release();
            throw;
        }
    }

    /// <summary>
    /// 取得した排他スロットを確実に解放するためのハンドル
    /// </summary>
    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref semaphore, null)?.Release();
        }
    }
}

/// <summary>
/// TSUTAYA DISCASのページをHTTPで取得し、レスポンスの文字コードに従ってHTMLを文字列へ変換する
/// </summary>
public sealed class DiscasPageFetcher
{
    private const string ChromeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    private readonly HttpClient httpClient;
    private readonly DiscasRequestThrottle requestThrottle;

    static DiscasPageFetcher()
    {
        // DISCASはShift_JIS系の文字コードを使用しているため、コードページ932を利用できるようにする。
        // .NETでは既定でコードページ系Encodingが有効ではないので、ライブラリ側で登録して
        // 呼び出し元の初期化方法に依存しないようにする。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// DISCASページ取得クライアントを初期化する
    /// </summary>
    /// <param name="httpClient">HTTP通信に使用するクライアント</param>
    /// <param name="requestThrottle">アプリ全体で共有するDISCASアクセス制御</param>
    public DiscasPageFetcher(HttpClient httpClient, DiscasRequestThrottle requestThrottle)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(requestThrottle);
        this.httpClient = httpClient;
        this.requestThrottle = requestThrottle;
    }

    /// <summary>
    /// テスト用にリクエスト間隔を指定してDISCASページ取得クライアントを初期化する
    /// </summary>
    /// <param name="httpClient">HTTP通信に使用するクライアント</param>
    /// <param name="minimumRequestInterval">リクエスト開始時刻の最低間隔</param>
    internal DiscasPageFetcher(HttpClient httpClient, TimeSpan minimumRequestInterval)
        : this(httpClient, new DiscasRequestThrottle(minimumRequestInterval))
    {
    }

    /// <summary>
    /// 指定されたDISCASページを取得する
    /// </summary>
    /// <param name="uri">取得対象の絶対URL</param>
    /// <param name="cancellationToken">取得処理を中断するためのトークン</param>
    /// <returns>HTTPステータス、最終URL、文字コード、HTML本文を含む取得結果</returns>
    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // CrawlerごとにFetcherインスタンスが分かれてもDISCASへの並列アクセスが発生しないよう、
        // アプリ全体で共有するThrottleをレスポンス本文の読み取り完了まで保持する。
        using var requestSlot = await requestThrottle.AcquireAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        // DISCAS側に通常のデスクトップブラウザからのアクセスと同等のUser-Agentを送る。
        // 独自CrawlerのUser-Agentによって通常ブラウザと異なるレスポンスになる可能性を避けるため固定している。
        request.Headers.UserAgent.ParseAdd(ChromeUserAgent);
        request.Headers.AcceptLanguage.ParseAdd("ja-JP,ja;q=0.9,en;q=0.5");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var charset = response.Content.Headers.ContentType?.CharSet;

        // ReadAsStringAsyncはContent-TypeのcharsetをそのままEncoding.GetEncodingへ渡すため、
        // DISCASが返す「Windows-31J」のような.NETで認識されない別名が含まれると例外になる。
        // そのため本文はバイト列で取得し、DISCAS固有のcharset表記をこちらでCP932へ正規化してからデコードする。
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var encoding = ResolveEncoding(charset);
        var html = encoding.GetString(bytes);

        return new FetchResult(
            response.StatusCode,
            response.RequestMessage?.RequestUri ?? uri,
            charset,
            html);
    }

    /// <summary>
    /// HTTPレスポンスのcharset表記から.NETで利用可能な文字コードを解決する
    /// </summary>
    /// <param name="charset">Content-Typeヘッダーで指定されたcharset。未指定の場合はnull</param>
    /// <returns>HTML本文のデコードに使用する文字コード</returns>
    internal static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            // HTTPヘッダーに指定がない場合は、現在のDISCAS検索ページで一般的なCP932を既定値とする。
            // 将来UTF-8へ移行した場合は、実レスポンスの確認結果に合わせて判定方法を見直す。
            return Encoding.GetEncoding(932);
        }

        var normalizedCharset = charset.Trim().Trim('"');

        // Windows-31JはCP932（Windows版Shift_JIS）を指す表記としてDISCASが使用しているが、
        // CodePagesEncodingProviderを登録してもこの別名自体は.NETで解決できないため明示的に対応する。
        if (normalizedCharset.Equals("Windows-31J", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.GetEncoding(932);
        }

        return Encoding.GetEncoding(normalizedCharset);
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
