using System.Net;
using System.Security.Cryptography;
using System.Text;
using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// DISCASで取得したジャケット画像をローカルへ安全にキャッシュし、DiscのImagePathを更新する
/// </summary>
public sealed class DiscImageCacheService(
    DiscaScoutDbContext dbContext,
    HttpClient httpClient,
    string imageDirectory,
    TimeSpan? minimumRequestInterval = null)
{
    private static readonly TimeSpan DefaultMinimumRequestInterval = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    private readonly TimeSpan requestInterval = minimumRequestInterval ?? DefaultMinimumRequestInterval;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private DateTimeOffset? lastRequestStartedAt;

    /// <summary>
    /// 指定したDISCAS IDのCDについて、現在のImageUrlに対応する画像キャッシュを同期する
    /// </summary>
    /// <remarks>
    /// 画像取得失敗はCD単位で結果へ記録し、既存ImagePathは変更しない。
    /// 新しい画像は一時ファイルへ保存してから本番ファイルへ移し、DB更新成功後に旧画像を削除する。
    /// </remarks>
    /// <param name="discasIds">同期対象のDISCAS商品ID</param>
    /// <param name="cancellationToken">同期処理を中断するためのトークン</param>
    /// <returns>同期件数と失敗件数</returns>
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

        var cached = 0;
        var skipped = 0;
        var cleared = 0;
        var failed = 0;

        foreach (var disc in discs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(disc.ImageUrl))
            {
                if (!string.IsNullOrWhiteSpace(disc.ImagePath))
                {
                    var oldPath = disc.ImagePath;
                    disc.ImagePath = null;
                    await dbContext.SaveChangesAsync(cancellationToken);
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

            var temporaryPath = targetPath + ".tmp";
            try
            {
                await DownloadAsync(new Uri(disc.ImageUrl, UriKind.Absolute), temporaryPath, cancellationToken);

                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(temporaryPath, targetPath);

                var oldPath = disc.ImagePath;
                disc.ImagePath = targetPath;
                await dbContext.SaveChangesAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(oldPath)
                    && !string.Equals(oldPath, targetPath, StringComparison.Ordinal))
                {
                    TryDelete(oldPath);
                }

                cached++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(temporaryPath);
                throw;
            }
            catch
            {
                // 画像取得の障害で通常スクレイピング結果まで失敗扱いにしない。
                // ImagePathを変更しないことで既存キャッシュを維持し、次回同期時に再試行できる。
                TryDelete(temporaryPath);
                failed++;
            }
        }

        return new DiscImageCacheResult(cached, skipped, cleared, failed);
    }

    private async Task DownloadAsync(Uri uri, string temporaryPath, CancellationToken cancellationToken)
    {
        await requestGate.WaitAsync(cancellationToken);
        try
        {
            if (lastRequestStartedAt is not null)
            {
                var remaining = requestInterval - (DateTimeOffset.UtcNow - lastRequestStartedAt.Value);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken);
                }
            }

            lastRequestStartedAt = DateTimeOffset.UtcNow;
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                temporaryPath,
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
        }
        finally
        {
            requestGate.Release();
        }
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
            // 孤立ファイルの清掃は将来の保守処理で扱えるよう、ここではデータ整合性を優先する。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。読み取り専用化など一時的なファイルシステム要因で新しいキャッシュまで無効にしない。
        }
    }
}

/// <summary>
/// ジャケット画像キャッシュ同期結果を保持する
/// </summary>
/// <param name="CachedCount">新規取得またはURL変更により更新した件数</param>
/// <param name="SkippedCount">既存キャッシュ利用または画像未登録で処理不要だった件数</param>
/// <param name="ClearedCount">画像未登録へ変化したためImagePathを解除した件数</param>
/// <param name="FailedCount">画像取得または保存に失敗し既存状態を維持した件数</param>
public sealed record DiscImageCacheResult(
    int CachedCount,
    int SkippedCount,
    int ClearedCount,
    int FailedCount);
