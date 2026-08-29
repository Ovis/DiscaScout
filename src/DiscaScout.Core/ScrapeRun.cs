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
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public long DurationMilliseconds { get; set; }
    public bool IsSuccess { get; set; }
    public int? FetchedCount { get; set; }
    public int? ParsedCount { get; set; }

    /// <summary>今回のクロールで取得したページ数。クロール完了前に失敗した場合はnull</summary>
    public int? PageCount { get; set; }

    public int AddedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int DeactivatedSourceCount { get; set; }

    /// <summary>失敗時の大分類。成功時はNone</summary>
    public ScrapeFailureType FailureType { get; set; }

    /// <summary>件数異常の場合の詳細理由</summary>
    public AbnormalCountReason? AbnormalCountReason { get; set; }

    /// <summary>
    /// 人間が明示的に許可した急減を使用して、このRunがDB反映まで成功したか
    /// </summary>
    public bool CountDropOverrideUsed { get; set; }

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

    public DateTime DueAt { get; set; }
    public ScrapeRetryStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}
