using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// Webから要求された長時間処理をSQLiteキューとして保存・更新する
/// </summary>
public sealed class ManualWorkStore(DiscaScoutDbContext dbContext)
{
    /// <summary>
    /// 通常の手動取得を重複しないようPendingで登録する
    /// </summary>
    /// <returns>新しい要求を登録した場合はtrue、通常取得系の処理が既に保留・実行中ならfalse</returns>
    public async Task<bool> TryEnqueueFullScrapeAsync(DateTime requestedAt, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.ManualWorkItems.AnyAsync(
            x => (x.Type == ManualWorkType.FullScrape || x.Type == ManualWorkType.CategoryScrape)
                && (x.Status == ManualWorkStatus.Pending || x.Status == ManualWorkStatus.Running),
            cancellationToken);
        if (exists)
        {
            return false;
        }

        dbContext.ManualWorkItems.Add(new ManualWorkItem
        {
            Type = ManualWorkType.FullScrape,
            Status = ManualWorkStatus.Pending,
            RequestedAt = requestedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// 指定カテゴリだけの通常取得を重複しないようPendingで登録する
    /// </summary>
    /// <remarks>
    /// 急減許可後の確認取得では無関係なカテゴリへ追加アクセスしないため、FullScrapeとは分けて登録する。
    /// </remarks>
    /// <returns>新しい要求を登録した場合はtrue、競合する通常取得系処理が既にある場合はfalse</returns>
    public async Task<bool> TryEnqueueCategoryScrapeAsync(
        ScrapeCategory category,
        DateTime requestedAt,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.ManualWorkItems.AnyAsync(
            x => (x.Type == ManualWorkType.FullScrape
                    || (x.Type == ManualWorkType.CategoryScrape && x.Category == category))
                && (x.Status == ManualWorkStatus.Pending || x.Status == ManualWorkStatus.Running),
            cancellationToken);
        if (exists)
        {
            return false;
        }

        dbContext.ManualWorkItems.Add(new ManualWorkItem
        {
            Type = ManualWorkType.CategoryScrape,
            Status = ManualWorkStatus.Pending,
            Category = category,
            RequestedAt = requestedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Artist全作品収集を同じ設定について重複しないようPendingで登録する
    /// </summary>
    /// <returns>新しい要求を登録した場合はtrue、同じArtist設定の処理が既に保留・実行中ならfalse</returns>
    public async Task<bool> TryEnqueueArtistCatalogAsync(
        long artistSettingId,
        DateTime requestedAt,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.ManualWorkItems.AnyAsync(
            x => x.Type == ManualWorkType.ArtistCatalog
                && x.ArtistSettingId == artistSettingId
                && (x.Status == ManualWorkStatus.Pending || x.Status == ManualWorkStatus.Running),
            cancellationToken);
        if (exists)
        {
            return false;
        }

        dbContext.ManualWorkItems.Add(new ManualWorkItem
        {
            Type = ManualWorkType.ArtistCatalog,
            Status = ManualWorkStatus.Pending,
            ArtistSettingId = artistSettingId,
            RequestedAt = requestedAt
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// 前回プロセス終了時にRunningだった処理をPendingへ戻す
    /// </summary>
    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        var interrupted = await dbContext.ManualWorkItems
            .Where(x => x.Status == ManualWorkStatus.Running)
            .ToListAsync(cancellationToken);
        foreach (var item in interrupted)
        {
            // Runningのままでは永久に再開されないため、単一インスタンス起動時に再キューする。
            // 各処理は完全スナップショット反映または冪等な設定単位収集なので、先頭からの再実行を許容する。
            item.Status = ManualWorkStatus.Pending;
            item.StartedAt = null;
            item.CompletedAt = null;
            item.FailureReason = null;
        }

        if (interrupted.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// 最も古いPending要求を取得する
    /// </summary>
    public Task<ManualWorkItem?> GetNextPendingAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.ManualWorkItems
            .AsNoTracking()
            .Where(x => x.Status == ManualWorkStatus.Pending)
            .OrderBy(x => x.RequestedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// 指定要求を実行中へ遷移させる
    /// </summary>
    public async Task MarkRunningAsync(long id, DateTime startedAt, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ManualWorkItems.SingleAsync(x => x.Id == id, cancellationToken);
        item.Status = ManualWorkStatus.Running;
        item.StartedAt = startedAt;
        item.CompletedAt = null;
        item.FailureReason = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 指定要求を成功終了へ遷移させる
    /// </summary>
    public async Task MarkCompletedAsync(long id, DateTime completedAt, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ManualWorkItems.SingleAsync(x => x.Id == id, cancellationToken);
        item.Status = ManualWorkStatus.Completed;
        item.CompletedAt = completedAt;
        item.FailureReason = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 指定要求を失敗終了へ遷移させる
    /// </summary>
    public async Task MarkFailedAsync(
        long id,
        DateTime completedAt,
        string? failureReason,
        CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ManualWorkItems.SingleAsync(x => x.Id == id, cancellationToken);
        item.Status = ManualWorkStatus.Failed;
        item.CompletedAt = completedAt;
        item.FailureReason = Truncate(failureReason);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 画面表示用に保留・実行中の要求を取得する
    /// </summary>
    public async Task<IReadOnlyList<ManualWorkItem>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.ManualWorkItems
            .AsNoTracking()
            .Where(x => x.Status == ManualWorkStatus.Pending || x.Status == ManualWorkStatus.Running)
            .OrderBy(x => x.RequestedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 画面表示用に直近の手動処理要求を新しい順で取得する
    /// </summary>
    public async Task<IReadOnlyList<ManualWorkItem>> GetRecentAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        return await dbContext.ManualWorkItems
            .AsNoTracking()
            .OrderByDescending(x => x.RequestedAt)
            .ThenByDescending(x => x.Id)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maxLength = 1000;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
