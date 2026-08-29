using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.Extensions.Hosting;

namespace DiscaScout.Web;

/// <summary>
/// 手動要求、定期スクレイピング、期限到来済みRetryを単一プロセス内で順次実行する
/// </summary>
public sealed class ScrapeBackgroundService(
    IServiceScopeFactory scopeFactory,
    ScrapeExecutionGate executionGate,
    ManualWorkSignal manualWorkSignal,
    ILogger<ScrapeBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeZoneInfo ScheduleTimeZone = ResolveScheduleTimeZone();

    /// <summary>
    /// アプリケーション起動中、手動要求と期限到来した処理を定期的に確認する
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedManualWorkAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessDueWorkAsync(stoppingToken);

            // 通常は1分ごとに定期実行・Retryを確認するが、Webから手動要求が追加された場合は
            // シグナルで即座に起床する。要求本体はSQLiteに残るためシグナルを失っても次回ポーリングで回収できる。
            await manualWorkSignal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    private async Task RecoverInterruptedManualWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var manualWorkStore = scope.ServiceProvider.GetRequiredService<ManualWorkStore>();
        await manualWorkStore.RecoverInterruptedAsync(cancellationToken);
    }

    private async Task ProcessDueWorkAsync(CancellationToken cancellationToken)
    {
        if (!await ProcessManualWorkAsync(cancellationToken))
        {
            return;
        }

        // 停止中に複数のRetryが期限到来している可能性があるため、期限済みがなくなるまで順次処理する。
        // 実際のDISCASアクセスは既存Fetcherでも直列化されるが、ここでも実行単位を並列化しない。
        while (true)
        {
            await using var retryScope = scopeFactory.CreateAsyncScope();
            var operationsStore = retryScope.ServiceProvider.GetRequiredService<IScrapeOperationsStore>();
            var retry = await operationsStore.GetNextDueRetryAsync(DateTime.UtcNow, cancellationToken);
            if (retry is null)
            {
                break;
            }

            var coordinator = retryScope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>();
            var result = await executionGate.TryRunAsync(
                ct => coordinator.ExecuteRetryAsync(retry, ct),
                cancellationToken);

            if (result is null)
            {
                return;
            }

            logger.LogInformation(
                "Retry completed. Category={Category}, Attempt={Attempt}, Success={Success}",
                retry.Category,
                retry.AttemptNumber,
                result.IsSuccess);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduleStore = scope.ServiceProvider.GetRequiredService<IScrapeScheduleStore>();
        var settings = await scheduleStore.GetAsync(cancellationToken);
        var dueLocalDate = ScrapeScheduleEvaluator.GetDueLocalDate(
            settings,
            DateTimeOffset.UtcNow,
            ScheduleTimeZone);

        if (dueLocalDate is null)
        {
            return;
        }

        var coordinatorForSchedule = scope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>();
        var execution = await executionGate.TryRunAsync(
            ct => coordinatorForSchedule.ExecuteAsync(ScrapeExecutionType.Scheduled, ct),
            cancellationToken);

        if (execution is null)
        {
            return;
        }

        // 失敗したカテゴリはScrapeRunCoordinatorがRetryを登録するため、定期枠自体は実行済みとして記録する。
        // ここを成功時だけ更新すると、失敗時に1分ごとに同じ全カテゴリ取得を繰り返してしまう。
        await scheduleStore.MarkScheduledExecutionAsync(dueLocalDate.Value, cancellationToken);
        logger.LogInformation("Scheduled scrape completed. LocalDate={LocalDate}, Success={Success}", dueLocalDate, execution.IsSuccess);
    }

    private async Task<bool> ProcessManualWorkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ManualWorkStore>();
            var item = await store.GetNextPendingAsync(cancellationToken);
            if (item is null)
            {
                return true;
            }

            try
            {
                var executed = await executionGate.TryRunAsync(
                    async ct =>
                    {
                        await store.MarkRunningAsync(item.Id, DateTime.UtcNow, ct);

                        switch (item.Type)
                        {
                            case ManualWorkType.FullScrape:
                            {
                                var coordinator = scope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>();
                                var result = await coordinator.ExecuteAsync(ScrapeExecutionType.Manual, ct);
                                if (result.IsSuccess)
                                {
                                    await store.MarkCompletedAsync(item.Id, DateTime.UtcNow, ct);
                                }
                                else
                                {
                                    await store.MarkFailedAsync(
                                        item.Id,
                                        DateTime.UtcNow,
                                        "手動取得は完了したが、失敗したカテゴリがある。実行履歴を確認すること。",
                                        ct);
                                }
                                break;
                            }
                            case ManualWorkType.ArtistCatalog:
                            {
                                if (item.ArtistSettingId is null)
                                {
                                    throw new InvalidOperationException("ArtistCatalog要求にArtistSettingIdが設定されていない");
                                }

                                var catalogService = scope.ServiceProvider.GetRequiredService<ArtistCatalogCollectionService>();
                                await catalogService.CollectAsync(item.ArtistSettingId.Value, ct);
                                await store.MarkCompletedAsync(item.Id, DateTime.UtcNow, ct);
                                break;
                            }
                            default:
                                throw new ArgumentOutOfRangeException(nameof(item.Type), item.Type, null);
                        }

                        return ManualWorkExecutionMarker.Instance;
                    },
                    cancellationToken);

                if (executed is null)
                {
                    // 別経路の取得が実行中ならPendingのまま残し、次回のシグナルまたはポーリングで再試行する。
                    return false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停止時はRunningを維持し、次回起動時のRecoverInterruptedAsyncでPendingへ戻す。
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manual work failed. WorkId={WorkId}, Type={Type}", item.Id, item.Type);

                // 実処理側のDbContextが例外後に不安定な状態になっている可能性を避けるため、
                // 失敗状態の記録は新しいScope/DbContextで行う。
                await using var failureScope = scopeFactory.CreateAsyncScope();
                var failureStore = failureScope.ServiceProvider.GetRequiredService<ManualWorkStore>();
                await failureStore.MarkFailedAsync(item.Id, DateTime.UtcNow, ex.Message, cancellationToken);
            }
        }
    }

    private static TimeZoneInfo ResolveScheduleTimeZone()
    {
        // DiscaScoutは日本国内のDISCAS運用を前提とするため、コンテナのTZ設定に依存せず日本時間で予定時刻を判定する。
        return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    }

    /// <summary>
    /// ScrapeExecutionGateの「実行できた / busyだった」をnullで判別するための参照型マーカー
    /// </summary>
    private sealed class ManualWorkExecutionMarker
    {
        public static ManualWorkExecutionMarker Instance { get; } = new();
    }
}
