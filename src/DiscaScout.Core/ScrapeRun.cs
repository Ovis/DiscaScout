namespace DiscaScout.Core;

/// <summary>
/// スクレイピング実行の起点を表す
/// </summary>
public enum ScrapeExecutionType
{
    Scheduled = 0,
    Manual = 1,
    Retry = 2
}

/// <summary>
/// スクレイピング対象カテゴリを表す
/// </summary>
public enum ScrapeCategory
{
    Upcoming = 0,
    New = 1
}

/// <summary>
/// リトライ予定の状態を表す
/// </summary>
public enum ScrapeRetryStatus
{
    Pending = 0,
    Completed = 1,
    Cancelled = 2
}

/// <summary>
/// カテゴリ単位のスクレイピング実行履歴を保持する
/// </summary>
public sealed class ScrapeRun
{
    public long Id { get; set; }
    public ScrapeExecutionType ExecutionType { get; set; }
    public ScrapeCategory Category { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long DurationMilliseconds { get; set; }
    public bool IsSuccess { get; set; }
    public int? FetchedCount { get; set; }
    public int? ParsedCount { get; set; }
    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeactivatedSourceCount { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>
/// 失敗したカテゴリの将来リトライ予定を保持する
/// </summary>
public sealed class ScrapeRetry
{
    public long Id { get; set; }
    public ScrapeCategory Category { get; set; }

    /// <summary>
    /// 1は3時間後の再試行、2はその翌日の最終再試行を表す
    /// </summary>
    public int AttemptNumber { get; set; }

    public DateTimeOffset DueAt { get; set; }
    public ScrapeRetryStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
