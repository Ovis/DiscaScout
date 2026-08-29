namespace DiscaScout.Core;

/// <summary>
/// Web画面から要求された長時間処理の種類を表す
/// </summary>
public enum ManualWorkType
{
    FullScrape = 1,
    ArtistCatalog = 2,
    CategoryScrape = 3
}

/// <summary>
/// 手動要求された長時間処理の実行状態を表す
/// </summary>
public enum ManualWorkStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// Webリクエストから切り離してBackgroundServiceで実行する手動処理を保持する
/// </summary>
public sealed class ManualWorkItem
{
    public long Id { get; set; }
    public ManualWorkType Type { get; set; }
    public ManualWorkStatus Status { get; set; }

    /// <summary>
    /// ArtistCatalog処理の場合に対象となるArtistSetting ID
    /// </summary>
    public long? ArtistSettingId { get; set; }

    /// <summary>
    /// CategoryScrape処理の場合に対象となる通常取得カテゴリ
    /// </summary>
    public ScrapeCategory? Category { get; set; }

    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
}
