using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// 定期スクレイピング設定を永続化する境界
/// </summary>
public interface IScrapeScheduleStore
{
    Task<ScrapeScheduleSettings> GetAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(bool isEnabled, DayOfWeek dayOfWeek, TimeOnly localTime, CancellationToken cancellationToken = default);
    Task MarkScheduledExecutionAsync(DateOnly localDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// SQLite上の単一設定行を読み書きする
/// </summary>
public sealed class ScrapeScheduleStore(DiscaScoutDbContext dbContext) : IScrapeScheduleStore
{
    /// <inheritdoc />
    public async Task<ScrapeScheduleSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.ScrapeScheduleSettings
            .SingleOrDefaultAsync(x => x.Id == ScrapeScheduleSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        // 初回起動直後に意図せずDISCASへアクセスしないよう、定期実行は無効状態で作成する。
        // Web設定画面から利用者が明示的に有効化した場合だけ実行する。
        settings = new ScrapeScheduleSettings();
        dbContext.ScrapeScheduleSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(
        bool isEnabled,
        DayOfWeek dayOfWeek,
        TimeOnly localTime,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(dayOfWeek))
        {
            throw new ArgumentOutOfRangeException(nameof(dayOfWeek));
        }

        var settings = await GetAsync(cancellationToken);
        settings.IsEnabled = isEnabled;
        settings.DayOfWeek = dayOfWeek;
        settings.LocalTime = localTime;

        // 曜日や時刻を変更しても「その日に既に実行した」という事実は維持する。
        // 設定変更直後の同日再実行を避け、必要な場合は手動実行を使う設計とする。
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkScheduledExecutionAsync(DateOnly localDate, CancellationToken cancellationToken = default)
    {
        var settings = await GetAsync(cancellationToken);
        settings.LastScheduledExecutionDate = localDate;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
