using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// 定期スクレイピング設定を永続化する境界
/// </summary>
public interface IScrapeScheduleStore
{
    Task<ScrapeScheduleSettings> GetAsync(CancellationToken cancellationToken = default);
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
        // Web設定画面が追加されるまではこの既定値を維持し、利用者が明示的に有効化した場合だけ実行する。
        settings = new ScrapeScheduleSettings();
        dbContext.ScrapeScheduleSettings.Add(settings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return settings;
    }

    /// <inheritdoc />
    public async Task MarkScheduledExecutionAsync(DateOnly localDate, CancellationToken cancellationToken = default)
    {
        var settings = await GetAsync(cancellationToken);
        settings.LastScheduledExecutionDate = localDate;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
