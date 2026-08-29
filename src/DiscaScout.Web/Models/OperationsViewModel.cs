using DiscaScout.Application;
using DiscaScout.Core;

namespace DiscaScout.Web.Models;

/// <summary>
/// 運用画面へ渡す定期実行設定と実行状態を保持する
/// </summary>
public sealed class OperationsViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public bool IsEnabled { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public TimeOnly LocalTime { get; init; }
    public DateOnly? LastScheduledExecutionDate { get; init; }
    public IReadOnlyList<ScrapeRun> RecentRuns { get; init; } = [];
    public IReadOnlyList<ScrapeRetry> PendingRetries { get; init; } = [];
    public IReadOnlyList<ManualWorkItem> ActiveManualWork { get; init; } = [];
    public IReadOnlyList<ManualWorkItem> RecentManualWork { get; init; } = [];
    public required DiscDetailFetchProgress DetailFetchProgress { get; init; }
    public string? StatusMessage { get; init; }
    public bool IsFullScrapeActive => ActiveManualWork.Any(x => x.Type is ManualWorkType.FullScrape or ManualWorkType.CategoryScrape);

    public static IReadOnlyList<(DayOfWeek Value, string Label)> DayOptions { get; } =
    [
        (System.DayOfWeek.Monday, "月曜日"),
        (System.DayOfWeek.Tuesday, "火曜日"),
        (System.DayOfWeek.Wednesday, "水曜日"),
        (System.DayOfWeek.Thursday, "木曜日"),
        (System.DayOfWeek.Friday, "金曜日"),
        (System.DayOfWeek.Saturday, "土曜日"),
        (System.DayOfWeek.Sunday, "日曜日")
    ];

    public static DateTime ToJapanTime(DateTime value) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone);

    public static string GetDayLabel(DayOfWeek value) => DayOptions.First(x => x.Value == value).Label;

    public static string GetManualWorkTypeLabel(ManualWorkType type) => type switch
    {
        ManualWorkType.FullScrape => "通常取得",
        ManualWorkType.CategoryScrape => "カテゴリ取得",
        ManualWorkType.ArtistCatalog => "Artist全作品",
        _ => type.ToString()
    };

    public static string GetManualWorkTarget(ManualWorkItem work) => work.Type switch
    {
        ManualWorkType.CategoryScrape => work.Category?.ToString() ?? "-",
        ManualWorkType.ArtistCatalog => work.ArtistSettingId?.ToString() ?? "-",
        _ => "-"
    };
}
