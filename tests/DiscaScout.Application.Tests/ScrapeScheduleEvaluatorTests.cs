using DiscaScout.Application;
using DiscaScout.Core;

namespace DiscaScout.Application.Tests;

/// <summary>
/// 定期スクレイピングの曜日・時刻・重複防止判定を検証する
/// </summary>
public sealed class ScrapeScheduleEvaluatorTests
{
    private static readonly TimeZoneInfo JapanTimeZone =
        TimeZoneInfo.CreateCustomTimeZone("Test/Japan", TimeSpan.FromHours(9), "Test/Japan", "Test/Japan");

    [Fact]
    public void GetDueLocalDate_無効設定なら実行しない()
    {
        var settings = CreateSettings(isEnabled: false);

        var result = ScrapeScheduleEvaluator.GetDueLocalDate(
            settings,
            new DateTimeOffset(2026, 8, 29, 1, 0, 0, TimeSpan.Zero),
            JapanTimeZone);

        Assert.Null(result);
    }

    [Fact]
    public void GetDueLocalDate_指定時刻前なら実行しない()
    {
        var settings = CreateSettings(localTime: new TimeOnly(10, 30));

        var result = ScrapeScheduleEvaluator.GetDueLocalDate(
            settings,
            new DateTimeOffset(2026, 8, 29, 1, 29, 0, TimeSpan.Zero),
            JapanTimeZone);

        Assert.Null(result);
    }

    [Fact]
    public void GetDueLocalDate_指定時刻を過ぎた同曜日なら当日を返す()
    {
        var settings = CreateSettings(localTime: new TimeOnly(10, 30));

        var result = ScrapeScheduleEvaluator.GetDueLocalDate(
            settings,
            new DateTimeOffset(2026, 8, 29, 1, 31, 0, TimeSpan.Zero),
            JapanTimeZone);

        Assert.Equal(new DateOnly(2026, 8, 29), result);
    }

    [Fact]
    public void GetDueLocalDate_同日の実行済みなら再実行しない()
    {
        var settings = CreateSettings(localTime: new TimeOnly(10, 30));
        settings.LastScheduledExecutionDate = new DateOnly(2026, 8, 29);

        var result = ScrapeScheduleEvaluator.GetDueLocalDate(
            settings,
            new DateTimeOffset(2026, 8, 29, 2, 0, 0, TimeSpan.Zero),
            JapanTimeZone);

        Assert.Null(result);
    }

    private static ScrapeScheduleSettings CreateSettings(
        bool isEnabled = true,
        TimeOnly? localTime = null)
    {
        return new ScrapeScheduleSettings
        {
            IsEnabled = isEnabled,
            DayOfWeek = DayOfWeek.Saturday,
            LocalTime = localTime ?? new TimeOnly(10, 0)
        };
    }
}
