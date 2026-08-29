using DiscaScout.Application;

namespace DiscaScout.Web;

/// <summary>
/// 未取得またはレンタル開始後のCD詳細情報を低速に補完するBackgroundService
/// </summary>
public sealed class DiscDetailMetadataBackgroundService(
    IServiceScopeFactory scopeFactory,
    DiscDetailFetchSignal signal,
    ScrapeExecutionGate scrapeExecutionGate,
    ILogger<DiscDetailMetadataBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RequestInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScrapeBusyWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 通常クロールを優先しながら詳細メタデータを1件ずつ補完する
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (scrapeExecutionGate.IsRunning)
            {
                // New/UpcomingやArtist Catalogの完全取得を優先し、長い通常クロールの最中に
                // 詳細補完が割り込んでページ取得時間を伸ばさないようにする。
                await Task.Delay(ScrapeBusyWait, stoppingToken);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<DiscDetailMetadataService>();

                long? discId = null;
                if (signal.TryDequeue(out var requestedId) && await service.IsDueAsync(requestedId, stoppingToken))
                {
                    discId = requestedId;
                }
                else
                {
                    discId = await service.GetNextDueDiscIdAsync(stoppingToken);
                }

                if (discId is null)
                {
                    await signal.WaitAsync(IdleScanInterval, stoppingToken);
                    continue;
                }

                await service.FetchAsync(discId.Value, stoppingToken);

                // 詳細補完は大量の既存CDを一度だけ埋める処理なので、共有Throttleに加えて
                // 1件ごとに明示的な休止を入れ、通常利用時のDISCAS負荷を低く保つ。
                await Task.Delay(RequestInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "DISCAS詳細メタデータのバックグラウンド取得に失敗しました");
                await Task.Delay(RequestInterval, stoppingToken);
            }
        }
    }
}
