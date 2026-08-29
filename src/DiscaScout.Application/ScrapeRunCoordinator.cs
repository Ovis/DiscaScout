using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// スクレイピング実行を履歴へ記録し、失敗時のリトライ予定を管理する
/// </summary>
public sealed class ScrapeRunCoordinator(
    DiscasScrapeService scrapeService,
    IScrapeOperationsStore operationsStore,
    TimeProvider? timeProvider = null)
{
    private static readonly DiscSourceCategory[] DefaultCategories =
    [
        DiscSourceCategory.Upcoming,
        DiscSourceCategory.New
    ];

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 定期または手動の通常実行として両カテゴリを順番に処理する
    /// </summary>
    /// <param name="executionType">ScheduledまたはManual</param>
    /// <param name="cancellationToken">実行を中断するためのトークン</param>
    /// <returns>カテゴリごとの実行結果</returns>
    public async Task<ScrapeExecutionResult> ExecuteAsync(
        ScrapeExecutionType executionType,
        CancellationToken cancellationToken = default)
    {
        if (executionType == ScrapeExecutionType.Retry)
        {
            throw new ArgumentException("RetryはExecuteRetryAsyncで実行する必要がある", nameof(executionType));
        }

        var results = new List<CategoryScrapeResult>(DefaultCategories.Length);
        foreach (var category in DefaultCategories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecuteAndRecordAsync(category, executionType, retry: null, cancellationToken));
        }

        return new ScrapeExecutionResult(results);
    }

    /// <summary>
    /// 期限到来済みのリトライ予定を1件実行する
    /// </summary>
    /// <param name="retry">実行するPending状態のリトライ予定</param>
    /// <param name="cancellationToken">実行を中断するためのトークン</param>
    /// <returns>対象カテゴリの実行結果</returns>
    public Task<CategoryScrapeResult> ExecuteRetryAsync(
        ScrapeRetry retry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (retry.Status != ScrapeRetryStatus.Pending)
        {
            throw new ArgumentException("Pendingではないリトライ予定は実行できない", nameof(retry));
        }

        return ExecuteAndRecordAsync(
            MapCategory(retry.Category),
            ScrapeExecutionType.Retry,
            retry,
            cancellationToken);
    }

    private async Task<CategoryScrapeResult> ExecuteAndRecordAsync(
        DiscSourceCategory category,
        ScrapeExecutionType executionType,
        ScrapeRetry? retry,
        CancellationToken cancellationToken)
    {
        var startedAtOffset = clock.GetUtcNow();
        var result = await scrapeService.ExecuteCategoryAsync(category, cancellationToken);
        var completedAtOffset = clock.GetUtcNow();
        var startedAt = startedAtOffset.UtcDateTime;
        var completedAt = completedAtOffset.UtcDateTime;

        // SQLiteではDateTimeOffsetの比較・ORDER BYに制約があるため、永続タイムスタンプはUTC DateTimeで統一する。
        // TimeProvider自体はDateTimeOffsetを返すので、永続化境界へ渡す直前にUTC DateTimeへ変換する。
        var run = new ScrapeRun
        {
            ExecutionType = executionType,
            Category = MapCategory(category),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMilliseconds = Math.Max(0, (long)(completedAtOffset - startedAtOffset).TotalMilliseconds),
            IsSuccess = result.IsSuccess,
            // 現在のCrawlerは完全なSnapshotが完成した時点でのみ件数を返すため、成功時は取得件数と解析件数が一致する。
            // 途中失敗時のページ単位件数はまだ公開していないので、誤った推定値を保存せずnullにする。
            FetchedCount = result.TotalCount,
            ParsedCount = result.TotalCount,
            AddedCount = result.AddedCount,
            UpdatedCount = result.UpdatedCount,
            DeactivatedSourceCount = result.DeactivatedSourceCount,
            FailureReason = result.IsSuccess ? null : TruncateFailureReason(result.ErrorMessage)
        };

        await operationsStore.AddRunAsync(run, cancellationToken);

        if (retry is not null)
        {
            // 実行済み予定を先に消費済みにすることで、失敗後に次段のRetryを登録しても
            // 同じ予定が再度期限到来として取得されないようにする。
            await operationsStore.CompleteRetryAsync(retry.Id, completedAt, cancellationToken);
        }

        if (result.IsSuccess)
        {
            // 定期・手動・Retryのいずれであっても最新実行が成功したなら、古い失敗を理由にした再試行は不要になる。
            await operationsStore.CancelPendingRetriesAsync(MapCategory(category), completedAt, cancellationToken);
            return result;
        }

        if (executionType != ScrapeExecutionType.Retry)
        {
            await operationsStore.EnsureRetryAsync(
                MapCategory(category),
                attemptNumber: 1,
                dueAt: completedAt.AddHours(3),
                now: completedAt,
                cancellationToken);
        }
        else if (retry!.AttemptNumber == 1)
        {
            // 3時間後の再試行にも失敗した場合だけ、翌日に最終試行を1回設ける。
            // 2回目まで失敗した後は連続アクセスを続けず、次回の通常実行に回復判定を委ねる。
            await operationsStore.EnsureRetryAsync(
                MapCategory(category),
                attemptNumber: 2,
                dueAt: completedAt.AddDays(1),
                now: completedAt,
                cancellationToken);
        }

        return result;
    }

    private static string? TruncateFailureReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        const int maxLength = 1000;
        return message.Length <= maxLength ? message : message[..maxLength];
    }

    private static ScrapeCategory MapCategory(DiscSourceCategory category) => category switch
    {
        DiscSourceCategory.Upcoming => ScrapeCategory.Upcoming,
        DiscSourceCategory.New => ScrapeCategory.New,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    private static DiscSourceCategory MapCategory(ScrapeCategory category) => category switch
    {
        ScrapeCategory.Upcoming => DiscSourceCategory.Upcoming,
        ScrapeCategory.New => DiscSourceCategory.New,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };
}
