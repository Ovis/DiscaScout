using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// スクレイピング安全装置の現在状態を永続化・参照する境界
/// </summary>
public interface IScrapeGuardStore
{
    /// <summary>指定カテゴリの安全装置設定を取得する</summary>
    Task<ScrapeGuardSettings> GetAsync(ScrapeCategory category, CancellationToken cancellationToken = default);

    /// <summary>指定カテゴリについて次回の正当な急減を1回だけ許可する</summary>
    Task EnableCountDropOverrideAsync(ScrapeCategory category, DateTime enabledAt, CancellationToken cancellationToken = default);

    /// <summary>指定カテゴリの急減許可を取り消す</summary>
    Task CancelCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default);

    /// <summary>
    /// 急減スナップショットのDB反映成功後に許可を消費する
    /// </summary>
    Task ConsumeCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Coreを使用してカテゴリ別のスクレイピング安全装置設定をSQLiteへ保存する
/// </summary>
public sealed class ScrapeGuardStore(DiscaScoutDbContext dbContext) : IScrapeGuardStore
{
    /// <inheritdoc />
    public async Task<ScrapeGuardSettings> GetAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.ScrapeGuardSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Category == category, cancellationToken);

        return settings ?? new ScrapeGuardSettings { Category = category };
    }

    /// <inheritdoc />
    public async Task EnableCountDropOverrideAsync(
        ScrapeCategory category,
        DateTime enabledAt,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetTrackedOrCreateAsync(category, cancellationToken);
        settings.IsCountDropOverrideEnabled = true;
        settings.CountDropOverrideEnabledAt = enabledAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CancelCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
    {
        var settings = await GetTrackedOrCreateAsync(category, cancellationToken);
        settings.IsCountDropOverrideEnabled = false;
        settings.CountDropOverrideEnabledAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task ConsumeCountDropOverrideAsync(ScrapeCategory category, CancellationToken cancellationToken = default)
    {
        // 通信・解析・DB反映の途中失敗では許可を失わないよう、消費は反映成功後にだけ行う。
        return CancelCountDropOverrideAsync(category, cancellationToken);
    }

    private async Task<ScrapeGuardSettings> GetTrackedOrCreateAsync(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        var settings = await dbContext.ScrapeGuardSettings
            .SingleOrDefaultAsync(x => x.Category == category, cancellationToken);
        if (settings is not null)
        {
            return settings;
        }

        settings = new ScrapeGuardSettings { Category = category };
        dbContext.ScrapeGuardSettings.Add(settings);
        return settings;
    }
}
