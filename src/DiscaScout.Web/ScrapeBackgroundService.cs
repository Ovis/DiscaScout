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
    private static readonly TimeZoneInfo ScheduleTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverInterruptedManualWorkAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessDueWorkAsync(stoppingToken);
            await manualWorkSignal.WaitAsync(PollInterval, stoppingToken);
        }
    }

    private async Task RecoverInterruptedManualWorkAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ManualWorkStore>().RecoverInterruptedAsync(cancellationToken);
    }

    private async Task ProcessDueWorkAsync(CancellationToken cancellationToken)
    {
        if (!await ProcessManualWorkAsync(cancellationToken)) return;

        while (true)
        {
            await using var retryScope = scopeFactory.CreateAsyncScope();
            var operationsStore = retryScope.ServiceProvider.GetRequiredService<IScrapeOperationsStore>();
            var retry = await operationsStore.GetNextDueRetryAsync(DateTime.UtcNow, cancellationToken);
            if (retry is null) break;

            var coordinator = retryScope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>();
            var result = await executionGate.TryRunAsync(ct => coordinator.ExecuteRetryAsync(retry, ct), cancellationToken);
            if (result is null) return;

            await retryScope.ServiceProvider.GetRequiredService<DiscordNotificationService>()
                .NotifyScrapeAsync(ScrapeExecutionType.Retry, result, result.NextRetryAt, cancellationToken);
            logger.LogInformation("Retry completed. Category={Category}, Attempt={Attempt}, Success={Success}", retry.Category, retry.AttemptNumber, result.IsSuccess);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var scheduleStore = scope.ServiceProvider.GetRequiredService<IScrapeScheduleStore>();
        var settings = await scheduleStore.GetAsync(cancellationToken);
        var dueLocalDate = ScrapeScheduleEvaluator.GetDueLocalDate(settings, DateTimeOffset.UtcNow, ScheduleTimeZone);
        if (dueLocalDate is null) return;

        var coordinatorForSchedule = scope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>();
        var execution = await executionGate.TryRunAsync(ct => coordinatorForSchedule.ExecuteAsync(ScrapeExecutionType.Scheduled, ct), cancellationToken);
        if (execution is null) return;

        await scheduleStore.MarkScheduledExecutionAsync(dueLocalDate.Value, cancellationToken);
        await NotifyExecutionAsync(scope.ServiceProvider, ScrapeExecutionType.Scheduled, execution, cancellationToken);
        logger.LogInformation("Scheduled scrape completed. LocalDate={LocalDate}, Success={Success}", dueLocalDate, execution.IsSuccess);
    }

    private async Task<bool> ProcessManualWorkAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ManualWorkStore>();
            var item = await store.GetNextPendingAsync(cancellationToken);
            if (item is null) return true;

            try
            {
                var executed = await executionGate.TryRunAsync(async ct =>
                {
                    await store.MarkRunningAsync(item.Id, DateTime.UtcNow, ct);
                    switch (item.Type)
                    {
                        case ManualWorkType.FullScrape:
                        {
                            var result = await scope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>()
                                .ExecuteAsync(ScrapeExecutionType.Manual, ct);
                            if (result.IsSuccess)
                                await store.MarkCompletedAsync(item.Id, DateTime.UtcNow, ct);
                            else
                                await store.MarkFailedAsync(item.Id, DateTime.UtcNow, "手動取得は完了したが、失敗したカテゴリがある。実行履歴を確認すること。", ct);

                            await NotifyExecutionAsync(scope.ServiceProvider, ScrapeExecutionType.Manual, result, ct);
                            break;
                        }
                        case ManualWorkType.CategoryScrape:
                        {
                            if (item.Category is null) throw new InvalidOperationException("CategoryScrape要求にCategoryが設定されていない");

                            var result = await scope.ServiceProvider.GetRequiredService<ScrapeRunCoordinator>()
                                .ExecuteManualCategoryAsync(item.Category.Value, ct);
                            if (result.IsSuccess)
                                await store.MarkCompletedAsync(item.Id, DateTime.UtcNow, ct);
                            else
                                await store.MarkFailedAsync(item.Id, DateTime.UtcNow, result.ErrorMessage ?? "カテゴリ取得に失敗した。実行履歴を確認すること。", ct);

                            await scope.ServiceProvider.GetRequiredService<DiscordNotificationService>()
                                .NotifyScrapeAsync(ScrapeExecutionType.Manual, result, result.NextRetryAt, ct);
                            break;
                        }
                        case ManualWorkType.ArtistCatalog:
                        {
                            if (item.ArtistSettingId is null) throw new InvalidOperationException("ArtistCatalog要求にArtistSettingIdが設定されていない");
                            await scope.ServiceProvider.GetRequiredService<ArtistCatalogCollectionService>().CollectAsync(item.ArtistSettingId.Value, ct);
                            await store.MarkCompletedAsync(item.Id, DateTime.UtcNow, ct);
                            break;
                        }
                        default:
                            throw new ArgumentOutOfRangeException(nameof(item.Type), item.Type, null);
                    }
                    return ManualWorkExecutionMarker.Instance;
                }, cancellationToken);

                if (executed is null) return false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Manual work failed. WorkId={WorkId}, Type={Type}", item.Id, item.Type);
                await using var failureScope = scopeFactory.CreateAsyncScope();
                await failureScope.ServiceProvider.GetRequiredService<ManualWorkStore>()
                    .MarkFailedAsync(item.Id, DateTime.UtcNow, ex.Message, cancellationToken);

                if (item.Type == ManualWorkType.ArtistCatalog && item.ArtistSettingId.HasValue)
                {
                    await failureScope.ServiceProvider.GetRequiredService<DiscordNotificationService>()
                        .NotifyArtistCatalogFailureAsync(item.ArtistSettingId.Value, ex.Message, cancellationToken);
                }
            }
        }
    }

    private static async Task NotifyExecutionAsync(IServiceProvider serviceProvider, ScrapeExecutionType executionType, ScrapeExecutionResult execution, CancellationToken cancellationToken)
    {
        var notifier = serviceProvider.GetRequiredService<DiscordNotificationService>();
        foreach (var result in execution.Categories)
        {
            await notifier.NotifyScrapeAsync(executionType, result, result.NextRetryAt, cancellationToken);
        }
    }

    /// <summary>ScrapeExecutionGateの「実行できた / busyだった」をnullで判別するための参照型マーカー</summary>
    private sealed class ManualWorkExecutionMarker
    {
        public static ManualWorkExecutionMarker Instance { get; } = new();
    }
}
