namespace DiscaScout.Core;

/// <summary>
/// 定期スクレイピングの実行条件と最終実行日を保持する
/// </summary>
public sealed class ScrapeScheduleSettings
{
    /// <summary>
    /// 単一設定行の固定ID
    /// </summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool IsEnabled { get; set; }
    public DayOfWeek DayOfWeek { get; set; } = DayOfWeek.Sunday;
    public TimeOnly LocalTime { get; set; } = new(4, 0);
    public DateOnly? LastScheduledExecutionDate { get; set; }
}
