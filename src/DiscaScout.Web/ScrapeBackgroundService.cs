using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.Extensions.Hosting;

namespace DiscaScout.Web;

/// <summary>
/// 定期スクレイピングと期限到来済みRetryを単一プロセス内で順次実行する
/// </summary>
public sealed class ScrapeBackgroundService(
    IServiceScopeFactory scopeFactory,
    ScrapeExecutionGate executionGate,
    ILogger<ScrapeBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeZoneInfo ScheduleTimeZone = ResolveScheduleTimeZone();

    /// <summary>
    /// アプリケーション起動中、期限到来した処理を定期的に確認する
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 起動直後にも判定することで、再起動が予定時刻をまたいだ場合や
        // 停止中にRetry期限を迎えた場合に次の1分を待たず回復処理へ入れる。
        await ProcessDueWorkAsync(stoppingToken);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessDueWorkAsync(stoppingToken);
        }
    }

    private async Task ProcessDueWorkAsync(CancellationToken cancellationToken)
    {
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
                // 将来Webからの手動実行が追加された際、実行中なら競合させず次のポーリングへ回す。
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

    private static TimeZoneInfo ResolveScheduleTimeZone()
    {
        // DiscaScoutは日本国内のDISCAS運用を前提とするため、コンテナのTZ設定に依存せず日本時間で予定時刻を判定する。
        return TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    }
}
