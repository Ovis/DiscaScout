using System.Net;
using System.Security.Cryptography;
using System.Text;
using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// DISCASで取得したジャケット画像をローカルへ安全にキャッシュし、DiscのImagePathを更新する
/// </summary>
public sealed class DiscImageCacheService
{
    private const int DefaultMaximumConcurrentDownloads = 4;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private readonly DiscaScoutDbContext dbContext;
    private readonly HttpClient httpClient;
    private readonly string imageDirectory;
    private readonly SemaphoreSlim downloadGate;
    private readonly TimeSpan minimumRequestInterval;
    private readonly SemaphoreSlim requestStartGate = new(1, 1);
    private DateTimeOffset? lastRequestStartedAt;

    /// <summary>
    /// 最大4並列で画像を取得する本番用サービスを初期化する
    /// </summary>
    public DiscImageCacheService(
        DiscaScoutDbContext dbContext,
        HttpClient httpClient,
        string imageDirectory)
        : this(dbContext, httpClient, imageDirectory, TimeSpan.Zero, DefaultMaximumConcurrentDownloads)
    {
    }

    /// <summary>
    /// テスト用に画像リクエスト開始間隔を指定して初期化する
    /// </summary>
    internal DiscImageCacheService(
        DiscaScoutDbContext dbContext,
        HttpClient httpClient,
        string imageDirectory,
        TimeSpan minimumRequestInterval)
        : this(dbContext, httpClient, imageDirectory, minimumRequestInterval, DefaultMaximumConcurrentDownloads)
    {
    }

    /// <summary>
    /// テスト用に開始間隔と最大並列数を指定して初期化する
    /// </summary>
    internal DiscImageCacheService(
        DiscaScoutDbContext dbContext,
        HttpClient httpClient,
        string imageDirectory,
        TimeSpan minimumRequestInterval,
        int maximumConcurrentDownloads)
    {
        if (minimumRequestInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        }
        if (maximumConcurrentDownloads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentDownloads));
        }

        this.dbContext = dbContext;
        this.httpClient = httpClient;
        this.imageDirectory = imageDirectory;
        this.minimumRequestInterval = minimumRequestInterval;
        downloadGate = new SemaphoreSlim(maximumConcurrentDownloads, maximumConcurrentDownloads);
    }

    /// <summary>
    /// 未取得またはURL変更済みの画像を最大件数まで同期する
    /// </summary>
    /// <param name="maxCount">1バッチで処理する最大CD数</param>
    /// <param name="cancellationToken">同期処理を中断するためのトークン</param>
    /// <returns>同期件数と失敗件数</returns>
    public async Task<DiscImageCacheResult> SyncPendingBatchAsync(
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }

        var candidates = await dbContext.Discs
            .AsNoTracking()
            .Where(x => x.ImageUrl != null || x.ImagePath != null)
            .OrderBy(x => x.Id)
            .Select(x => new { x.DiscasId, x.ImageUrl, x.ImagePath })
            .ToListAsync(cancellationToken);

        var pendingIds = candidates
            .Where(x => IsPending(x.DiscasId, x.ImageUrl, x.ImagePath))
            .Take(maxCount)
            .Select(x => x.DiscasId)
            .ToArray();

        return await SyncAsync(pendingIds, cancellationToken);
    }

    /// <summary>
    /// 指定したDISCAS IDのCDについて、現在のImageUrlに対応する画像キャッシュを同期する
    /// </summary>
    /// <remarks>
    /// HTTP取得だけを最大4並列で行い、EF Coreの追跡エンティティ更新は並列化しない。
    /// これにより一般的なブラウザの画像読み込みに近い並列度を許容しつつ、DbContextのスレッド安全性を維持する。
    /// </remarks>
    public async Task<DiscImageCacheResult> SyncAsync(
        IEnumerable<string> discasIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discasIds);

        var ids = discasIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return new DiscImageCacheResult(0, 0, 0, 0);
        }

        Directory.CreateDirectory(imageDirectory);

        var discs = await dbContext.Discs
            .Where(x => ids.Contains(x.DiscasId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var skipped = 0;
        var cleared = 0;
        var downloadTargets = new List<ImageDownloadTarget>();

        foreach (var disc in discs)
        {
            if (string.IsNullOrWhiteSpace(disc.ImageUrl))
            {
                if (!string.IsNullOrWhiteSpace(disc.ImagePath))
                {
                    var oldPath = disc.ImagePath;
                    disc.ImagePath = null;
                    TryDelete(oldPath);
                    cleared++;
                }
                else
                {
                    skipped++;
                }
                continue;
            }

            var targetPath = BuildTargetPath(disc.DiscasId, disc.ImageUrl);
            if (string.Equals(disc.ImagePath, targetPath, StringComparison.Ordinal)
                && File.Exists(targetPath))
            {
                skipped++;
                continue;
            }

            downloadTargets.Add(new ImageDownloadTarget(
                disc.Id,
                disc.ImageUrl,
                disc.ImagePath,
                targetPath,
                targetPath + ".tmp"));
        }

        if (cleared > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var results = await Task.WhenAll(downloadTargets.Select(x => DownloadTargetAsync(x, cancellationToken)));
        var cached = 0;
        var failed = 0;

        foreach (var result in results)
        {
            var disc = discs.Single(x => x.Id == result.Target.DiscId);
            if (!result.IsSuccess)
            {
                failed++;
                continue;
            }

            if (File.Exists(result.Target.TargetPath))
            {
                File.Delete(result.Target.TargetPath);
            }
            File.Move(result.Target.TemporaryPath, result.Target.TargetPath);

            disc.ImagePath = result.Target.TargetPath;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.Target.OldPath)
                && !string.Equals(result.Target.OldPath, result.Target.TargetPath, StringComparison.Ordinal))
            {
                TryDelete(result.Target.OldPath);
            }
            cached++;
        }

        return new DiscImageCacheResult(cached, skipped, cleared, failed);
    }

    private async Task<ImageDownloadResult> DownloadTargetAsync(
        ImageDownloadTarget target,
        CancellationToken cancellationToken)
    {
        await downloadGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await WaitForOptionalStartIntervalAsync(cancellationToken);
                using var response = await httpClient.GetAsync(
                    new Uri(target.ImageUrl, UriKind.Absolute),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                {
                    throw new HttpRequestException($"画像取得に失敗した: HTTP {(int)response.StatusCode} {response.StatusCode}");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"画像ではないContent-Typeが返された: {mediaType ?? "(none)"}");
                }

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    target.TemporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output.Length == 0)
                {
                    throw new InvalidDataException("取得した画像が0バイトだった");
                }

                return new ImageDownloadResult(target, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(target.TemporaryPath);
                throw;
            }
            catch
            {
                // 画像取得失敗は検索結果の完全性とは独立しているため、次回BackgroundService実行で再試行する。
                TryDelete(target.TemporaryPath);
                return new ImageDownloadResult(target, false);
            }
        }
        finally
        {
            downloadGate.Release();
        }
    }

    private async Task WaitForOptionalStartIntervalAsync(CancellationToken cancellationToken)
    {
        if (minimumRequestInterval <= TimeSpan.Zero)
        {
            return;
        }

        await requestStartGate.WaitAsync(cancellationToken);
        try
        {
            if (lastRequestStartedAt is not null)
            {
                var remaining = minimumRequestInterval - (DateTimeOffset.UtcNow - lastRequestStartedAt.Value);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }
            lastRequestStartedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            requestStartGate.Release();
        }
    }

    private bool IsPending(string discasId, string? imageUrl, string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return !string.IsNullOrWhiteSpace(imagePath);
        }

        var targetPath = BuildTargetPath(discasId, imageUrl);
        return !string.Equals(imagePath, targetPath, StringComparison.Ordinal) || !File.Exists(targetPath);
    }

    private string BuildTargetPath(string discasId, string imageUrl)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageUrl)))[..16].ToLowerInvariant();
        var extension = GetSafeExtension(imageUrl);
        return Path.Combine(imageDirectory, $"{discasId}-{hash}{extension}");
    }

    private static string GetSafeExtension(string imageUrl)
    {
        var extension = Path.GetExtension(new Uri(imageUrl, UriKind.Absolute).AbsolutePath);
        return AllowedExtensions.Contains(extension) ? extension.ToLowerInvariant() : ".img";
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // DBは既に新しいImagePathへ切り替わっているため、旧ファイル削除失敗で処理全体を戻さない。
        }
        catch (UnauthorizedAccessException)
        {
            // 一時的なファイルシステム要因で新しいキャッシュまで無効にしない。
        }
    }

    private sealed record ImageDownloadTarget(
        long DiscId,
        string ImageUrl,
        string? OldPath,
        string TargetPath,
        string TemporaryPath);

    private sealed record ImageDownloadResult(ImageDownloadTarget Target, bool IsSuccess);
}

/// <summary>
/// ジャケット画像キャッシュ同期結果を保持する
/// </summary>
public sealed record DiscImageCacheResult(
    int CachedCount,
    int SkippedCount,
    int ClearedCount,
    int FailedCount)
{
    /// <summary>このバッチで実際に処理対象となった件数</summary>
    public int ProcessedCount => CachedCount + ClearedCount + FailedCount;
}
