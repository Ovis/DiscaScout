namespace DiscaScout.Core;

/// <summary>
/// スクレイピング失敗の大分類を表す
/// </summary>
public enum ScrapeFailureType
{
    None = 0,
    ProcessingError = 1,
    AbnormalCount = 2
}

/// <summary>
/// 件数異常として取得結果を拒否した理由を表す
/// </summary>
public enum AbnormalCountReason
{
    ZeroCount = 1,
    CountDrop = 2
}

/// <summary>
/// カテゴリ単位のスクレイピング安全装置の状態を保持する
/// </summary>
public sealed class ScrapeGuardSettings
{
    /// <summary>安全装置を適用するカテゴリ</summary>
    public ScrapeCategory Category { get; set; }

    /// <summary>
    /// 次に70%未満の完全スナップショットを取得した場合だけ、その急減を受け入れるか
    /// </summary>
    public bool IsCountDropOverrideEnabled { get; set; }

    /// <summary>急減許可を有効化したUTC日時</summary>
    public DateTime? CountDropOverrideEnabledAt { get; set; }
}
