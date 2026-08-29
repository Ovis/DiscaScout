using DiscaScout.Core;

namespace DiscaScout.Application;

/// <summary>
/// 現在時刻と保存済み設定から定期スクレイピングを開始すべきか判定する
/// </summary>
public static class ScrapeScheduleEvaluator
{
    /// <summary>
    /// 指定時刻で定期実行が期限到来しているか判定する
    /// </summary>
    /// <param name="settings">保存済みスケジュール設定</param>
    /// <param name="now">現在時刻</param>
    /// <param name="timeZone">スケジュール判定に使用するタイムゾーン</param>
    /// <returns>期限到来している場合はローカル日付、それ以外はnull</returns>
    public static DateOnly? GetDueLocalDate(
        ScrapeScheduleSettings settings,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeZone);

        if (!settings.IsEnabled)
        {
            return null;
        }

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        if (localNow.DayOfWeek != settings.DayOfWeek || localDate == settings.LastScheduledExecutionDate)
        {
            return null;
        }

        // コンテナ起動が指定時刻より少し遅れた場合でも、その曜日のうちであれば一度だけ追いついて実行する。
        // LastScheduledExecutionDateで同日の再実行を防ぐため、BackgroundServiceのポーリング間隔に依存しない。
        return TimeOnly.FromDateTime(localNow.DateTime) >= settings.LocalTime
            ? localDate
            : null;
    }
}
