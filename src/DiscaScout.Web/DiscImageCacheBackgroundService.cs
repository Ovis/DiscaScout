using DiscaScout.Persistence;

namespace DiscaScout.Web;

/// <summary>
/// 未取得ジャケット画像を検索処理とは独立して段階的に補完する
/// </summary>
public sealed class DiscImageCacheBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<DiscImageCacheBackgroundService> logger) : BackgroundService
{
    private const int BatchSize = 40;
    private const int BurstBatchCount = 10;
    private static readonly TimeSpan BatchInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// DB上で未取得となっている画像を40件ずつ最大4並列で補完する
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<string> pendingIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var imageCache = scope.ServiceProvider.GetRequiredService<DiscImageCacheService>();
                pendingIds = await imageCache.GetPendingDiscasIdsAsync(stoppingToken);
            }

            if (pendingIds.Count == 0)
            {
                await Task.Delay(IdleInterval, stoppingToken);
                continue;
            }

            var batchesSincePause = 0;
            foreach (var batch in pendingIds.Chunk(BatchSize))
            {
                stoppingToken.ThrowIfCancellationRequested();

                await using var scope = scopeFactory.CreateAsyncScope();
                var imageCache = scope.ServiceProvider.GetRequiredService<DiscImageCacheService>();
                var result = await imageCache.SyncAsync(batch, stoppingToken);

                logger.LogInformation(
                    "Image cache batch completed. Cached={Cached}, Failed={Failed}, Cleared={Cleared}",
                    result.CachedCount,
                    result.FailedCount,
                    result.ClearedCount);

                batchesSincePause++;
                if (batchesSincePause >= BurstBatchCount)
                {
                    // 40件単位の画像ロードを長時間連続させないため、10バッチごとに追加休止する。
                    // 休止時間を固定せず5～20秒とすることで、周期的な集中アクセスも避ける。
                    await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 21)), stoppingToken);
                    batchesSincePause = 0;
                }
                else
                {
                    await Task.Delay(BatchInterval, stoppingToken);
                }
            }

            // 失敗画像は同じ巡回中には再試行せず、全候補を一巡した後に次のスキャンで扱う。
            await Task.Delay(IdleInterval, stoppingToken);
        }
    }
}
