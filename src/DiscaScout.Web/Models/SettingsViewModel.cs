using DiscaScout.Core;

namespace DiscaScout.Web.Models;

/// <summary>
/// 設定画面へ渡すDiscord設定とスクレイピング安全装置状態を保持する
/// </summary>
public sealed class SettingsViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public DiscordNotificationMode DiscordMode { get; init; }
    public string? DiscordWebhookUrl { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<ScrapeGuardStatus> ScrapeGuards { get; init; } = [];
    public ScrapeGuardStatus? CountDropConfirmation { get; init; }

    public static DateTime ToJapanTime(DateTime value) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone);

    public static string GetCategoryLabel(ScrapeCategory category) => category switch
    {
        ScrapeCategory.Upcoming => "近日リリース",
        ScrapeCategory.New => "新作",
        _ => category.ToString()
    };

    public sealed record ScrapeGuardStatus(
        ScrapeCategory Category,
        ScrapeGuardSettings Settings,
        ScrapeRun? Baseline,
        ScrapeRun? LatestAnomaly);
}
