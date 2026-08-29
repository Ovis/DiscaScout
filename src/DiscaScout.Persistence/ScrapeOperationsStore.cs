using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// スクレイピング実行履歴とリトライ予定を更新する永続化境界
/// </summary>
public interface IScrapeOperationsStore
{
    Task AddRunAsync(ScrapeRun run, CancellationToken cancellationToken = default);
    Task EnsureRetryAsync(ScrapeCategory category, int attemptNumber, DateTimeOffset dueAt, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task CancelPendingRetriesAsync(ScrapeCategory category, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default);
    Task CompleteRetryAsync(long retryId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default);
    Task<ScrapeRetry?> GetNextDueRetryAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>
/// Web運用画面で表示するスクレイピング状態を読み取る境界
/// </summary>
public interface IScrapeOperationsQueryStore
{
    Task<IReadOnlyList<ScrapeRun>> GetRecentRunsAsync(int count, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScrapeRetry>> GetPendingRetriesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// EF Coreを使用して実行履歴とリトライ予定をSQLiteへ保存・照会する
/// </summary>
public sealed class ScrapeOperationsStore(DiscaScoutDbContext dbContext) : IScrapeOperationsStore, IScrapeOperationsQueryStore
{
    /// <inheritdoc />
    public async Task AddRunAsync(ScrapeRun run, CancellationToken cancellationToken = default)
    {
        dbContext.ScrapeRuns.Add(run);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureRetryAsync(
        ScrapeCategory category,
        int attemptNumber,
        DateTimeOffset dueAt,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.ScrapeRetries.AnyAsync(
            x => x.Category == category && x.Status == ScrapeRetryStatus.Pending,
            cancellationToken);
        if (exists)
        {
            // 同じカテゴリに既に保留中の予定がある場合は重複登録しない。
            // 定期実行と手動実行が近接して失敗しても、同じ再試行を複数回発火させないための制約である。
            return;
        }

        dbContext.ScrapeRetries.Add(new ScrapeRetry
        {
            Category = category,
            AttemptNumber = attemptNumber,
            DueAt = dueAt,
            Status = ScrapeRetryStatus.Pending,
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CancelPendingRetriesAsync(
        ScrapeCategory category,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default)
    {
        var retries = await dbContext.ScrapeRetries
            .Where(x => x.Category == category && x.Status == ScrapeRetryStatus.Pending)
            .ToListAsync(cancellationToken);

        foreach (var retry in retries)
        {
            retry.Status = ScrapeRetryStatus.Cancelled;
            retry.ResolvedAt = resolvedAt;
        }

        if (retries.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task CompleteRetryAsync(long retryId, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        var retry = await dbContext.ScrapeRetries.SingleOrDefaultAsync(x => x.Id == retryId, cancellationToken);
        if (retry is null || retry.Status != ScrapeRetryStatus.Pending)
        {
            return;
        }

        retry.Status = ScrapeRetryStatus.Completed;
        retry.ResolvedAt = resolvedAt;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ScrapeRetry?> GetNextDueRetryAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var pendingRetries = await dbContext.ScrapeRetries
            .AsNoTracking()
            .Where(x => x.Status == ScrapeRetryStatus.Pending)
            .ToListAsync(cancellationToken);

        // SQLiteのEF CoreプロバイダーはDateTimeOffsetの大小比較やORDER BYをSQLへ変換できない。
        // Retryはカテゴリごとに最大1件しかPendingにならない設計なので、Pending行だけDBで絞り込み、
        // 日時条件と並び替えはメモリ上で行っても件数・性能ともに問題にならない。
        return pendingRetries
            .Where(x => x.DueAt <= now)
            .OrderBy(x => x.DueAt)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeRun>> GetRecentRunsAsync(
        int count,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var runs = await dbContext.ScrapeRuns
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // SQLiteではDateTimeOffsetのORDER BYもSQL変換できないため、運用画面用の小規模な履歴一覧は
        // メモリ上で新しい順に並べ替える。将来履歴件数が大きくなる場合はUTC ticks等の専用列を検討する。
        return runs
            .OrderByDescending(x => x.StartedAt)
            .Take(count)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScrapeRetry>> GetPendingRetriesAsync(CancellationToken cancellationToken = default)
    {
        var pendingRetries = await dbContext.ScrapeRetries
            .AsNoTracking()
            .Where(x => x.Status == ScrapeRetryStatus.Pending)
            .ToListAsync(cancellationToken);

        // Pending件数は極小なので、SQLiteが未対応のDateTimeOffset並び替えだけメモリ側で行う。
        return pendingRetries
            .OrderBy(x => x.DueAt)
            .ToList();
    }
}
