using System.Net;
using System.Text;

namespace DiscaScout.Scraping;

/// <summary>
/// すべてのDISCAS検索ページHTTPアクセスで共有するリクエスト間隔と排他制御を提供する
/// </summary>
public sealed class DiscasRequestThrottle
{
    private static readonly TimeSpan DefaultMinimumRequestInterval = TimeSpan.FromSeconds(2);
    private const int DefaultBurstSize = 10;

    private readonly TimeSpan minimumRequestInterval;
    private readonly int burstSize;
    private readonly Func<TimeSpan> burstPauseFactory;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset? lastRequestStartedAt;
    private int requestsSinceBurstPause;

    /// <summary>
    /// 本番用の最低2秒間隔と10ページごとの追加休止でスロットルを初期化する
    /// </summary>
    public DiscasRequestThrottle()
        : this(
            DefaultMinimumRequestInterval,
            DefaultBurstSize,
            static () => TimeSpan.FromSeconds(Random.Shared.Next(5, 21)))
    {
    }

    /// <summary>
    /// テスト用にリクエスト開始間隔だけを指定して初期化する
    /// </summary>
    /// <param name="minimumRequestInterval">リクエスト開始時刻の最低間隔</param>
    internal DiscasRequestThrottle(TimeSpan minimumRequestInterval)
        : this(minimumRequestInterval, int.MaxValue, static () => TimeSpan.Zero)
    {
    }

    /// <summary>
    /// テスト用に通常間隔、連続取得数、追加休止時間を指定して初期化する
    /// </summary>
    internal DiscasRequestThrottle(
        TimeSpan minimumRequestInterval,
        int burstSize,
        Func<TimeSpan> burstPauseFactory)
    {
        if (minimumRequestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        }
        if (burstSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(burstSize));
        }

        this.minimumRequestInterval = minimumRequestInterval;
        this.burstSize = burstSize;
        this.burstPauseFactory = burstPauseFactory ?? throw new ArgumentNullException(nameof(burstPauseFactory));
    }

    /// <summary>
    /// 次のDISCAS検索ページを開始できるまで待機し、排他スロットを取得する
    /// </summary>
    /// <remarks>
    /// 検索ページは常に直列化し開始時刻を最低2秒空ける。さらに10ページ連続で取得した後は
    /// 5～20秒の追加休止を入れ、機械的な定間隔アクセスが長時間継続しないようにする。
    /// </remarks>
    /// <param name="cancellationToken">待機を中断するためのトークン</param>
    /// <returns>HTTPレスポンス処理完了時に破棄する排他スロット</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            if (requestsSinceBurstPause >= burstSize)
            {
                var burstPause = burstPauseFactory();
                if (burstPause > TimeSpan.Zero)
                {
                    await Task.Delay(burstPause, cancellationToken);
                }
                requestsSinceBurstPause = 0;
            }

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
            requestsSinceBurstPause++;
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// DISCASページ取得クライアントを初期化する
    /// </summary>
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
    internal DiscasPageFetcher(HttpClient httpClient, TimeSpan minimumRequestInterval)
        : this(httpClient, new DiscasRequestThrottle(minimumRequestInterval))
    {
    }

    /// <summary>
    /// 指定されたDISCASページを取得する
    /// </summary>
    public async Task<FetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // CategoryとArtist Catalogが同時に動いても検索ページを並列取得しないため、
        // アプリ全体で共有するThrottleをレスポンス本文の読み取り完了まで保持する。
        using var requestSlot = await requestThrottle.AcquireAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(ChromeUserAgent);
        request.Headers.AcceptLanguage.ParseAdd("ja-JP,ja;q=0.9,en;q=0.5");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var charset = response.Content.Headers.ContentType?.CharSet;
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
    internal static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.GetEncoding(932);
        }

        var normalizedCharset = charset.Trim().Trim('"');
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
public sealed record FetchResult(
    HttpStatusCode StatusCode,
    Uri FinalUri,
    string? Charset,
    string Html);
