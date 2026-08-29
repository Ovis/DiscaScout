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
    public Task<CategoryScrapeResult> ExecuteRetryAsync(ScrapeRetry retry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retry);
        if (retry.Status != ScrapeRetryStatus.Pending)
        {
            throw new ArgumentException("Pendingではないリトライ予定は実行できない", nameof(retry));
        }

        return ExecuteAndRecordAsync(MapCategory(retry.Category), ScrapeExecutionType.Retry, retry, cancellationToken);
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

        var run = new ScrapeRun
        {
            ExecutionType = executionType,
            Category = MapCategory(category),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMilliseconds = Math.Max(0, (long)(completedAtOffset - startedAtOffset).TotalMilliseconds),
            IsSuccess = result.IsSuccess,
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
            // 実行済み予定を先に消費済みにしてから次段のRetryを登録する。
            await operationsStore.CompleteRetryAsync(retry.Id, completedAt, cancellationToken);
        }

        if (result.IsSuccess)
        {
            await operationsStore.CancelPendingRetriesAsync(MapCategory(category), completedAt, cancellationToken);
            return result with { NextRetryAt = null };
        }

        DateTime? nextRetryAt = null;
        if (executionType != ScrapeExecutionType.Retry)
        {
            nextRetryAt = completedAt.AddHours(3);
            await operationsStore.EnsureRetryAsync(MapCategory(category), 1, nextRetryAt.Value, completedAt, cancellationToken);
        }
        else if (retry!.AttemptNumber == 1)
        {
            // 3時間後の再試行にも失敗した場合だけ翌日に最終試行を1回設ける。
            nextRetryAt = completedAt.AddDays(1);
            await operationsStore.EnsureRetryAsync(MapCategory(category), 2, nextRetryAt.Value, completedAt, cancellationToken);
        }

        // 通知側がDBを再検索せず、実際にこの実行で登録した次回Retry日時を表示できるよう結果へ含める。
        return result with { NextRetryAt = nextRetryAt };
    }

    private static string? TruncateFailureReason(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
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
