using System.Net;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscaScout.Application;

/// <summary>DISCASのジャンルマスターを取得・検証し、ローカルマスターへ差分反映する</summary>
public sealed class GenreMasterService(
    DiscaScoutDbContext dbContext,
    DiscasPageFetcher fetcher,
    DiscasGenreMasterParser parser,
    ILogger<GenreMasterService> logger)
{
    private static readonly Uri GenreMasterUri = new("https://movie-tsutaya.tsite.jp/netdvd/cd/genreAll.do");
    private const int MinimumAcceptedPercent = 75;

    /// <summary>ジャンルマスターが空の場合だけ、通常クロール前の初期取得を行う</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (await dbContext.Genres.AnyAsync(cancellationToken)) return;
        await RefreshAsync(cancellationToken);
    }

    /// <summary>現在保持しているジャンルマスターの件数と最終正常更新日時を取得する</summary>
    public async Task<GenreMasterStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var active = await dbContext.Genres.CountAsync(x => x.IsActive, cancellationToken);
        var inactive = await dbContext.Genres.CountAsync(x => !x.IsActive, cancellationToken);
        var lastUpdatedAt = await dbContext.GenreMasterStates.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => x.LastUpdatedAt)
            .SingleOrDefaultAsync(cancellationToken);
        return new GenreMasterStatus(active, inactive, lastUpdatedAt);
    }

    /// <summary>DISCASのジャンルマスターを手動更新する</summary>
    public async Task<GenreMasterUpdateResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        // 外部取得とHTML解析はDBトランザクションより前に完了させる。
        // DISCAS応答待ちの間にSQLiteの書き込みロックを保持しないためである。
        var response = await fetcher.FetchAsync(GenreMasterUri, cancellationToken);
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            throw new InvalidOperationException($"ジャンルマスターの取得に失敗した: {(int)response.StatusCode} {response.StatusCode}");

        var scraped = parser.Parse(response.Html);
        Validate(scraped);

        var currentActiveCount = await dbContext.Genres.CountAsync(x => x.IsActive, cancellationToken);
        if (currentActiveCount > 0 && (long)scraped.Count * 100 < (long)currentActiveCount * MinimumAcceptedPercent)
            throw new InvalidOperationException($"ジャンル件数が前回の75%未満のため更新を中止した: 現在 {currentActiveCount}件 / 取得 {scraped.Count}件");

        var now = DateTime.UtcNow;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var existing = await dbContext.Genres.ToListAsync(cancellationToken);
        var byExternalId = existing.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        var seen = scraped.Select(x => x.ExternalId).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        var deactivated = 0;
        var reactivated = 0;
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in scraped)
        {
            if (!byExternalId.TryGetValue(item.ExternalId, out var genre))
            {
                genre = new Genre
                {
                    ExternalId = item.ExternalId,
                    Name = item.Name,
                    SortOrder = item.SortOrder,
                    IsActive = true,
                    FirstSeenAt = now,
                    LastSeenAt = now
                };
                dbContext.Genres.Add(genre);
                byExternalId.Add(item.ExternalId, genre);
                added++;
            }
            else
            {
                if (!genre.IsActive) reactivated++;
                if (genre.Name != item.Name || genre.SortOrder != item.SortOrder) updatedIds.Add(item.ExternalId);
                genre.Name = item.Name;
                genre.SortOrder = item.SortOrder;
                genre.IsActive = true;
                genre.LastSeenAt = now;
            }
        }

        // 新規Genreの内部IDを確定してからParentIdを設定する。
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var item in scraped)
        {
            var genre = byExternalId[item.ExternalId];
            var parentId = item.ParentExternalId is null ? null : byExternalId[item.ParentExternalId].Id;
            if (genre.ParentId != parentId)
            {
                genre.ParentId = parentId;
                if (existing.Contains(genre)) updatedIds.Add(item.ExternalId);
            }
        }

        foreach (var genre in existing.Where(x => x.IsActive && !seen.Contains(x.ExternalId)))
        {
            genre.IsActive = false;
            deactivated++;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        var inactiveIds = await dbContext.Genres.AsNoTracking()
            .Where(x => !x.IsActive)
            .Select(x => x.Id)
            .ToArrayAsync(cancellationToken);

        // マスター更新で解決可能になった可能性がある未解決Discと、廃止ジャンルを参照するDiscだけを
        // 詳細取得キューへ戻す。通常の有効ジャンルを持つDiscは再取得しない。
        await dbContext.Discs
            .Where(x => x.GenreId == null || (x.GenreId != null && inactiveIds.Contains(x.GenreId.Value)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DetailRefreshCompleted, false)
                .SetProperty(x => x.DetailFetchedAt, (DateTime?)null)
                .SetProperty(x => x.DetailLastAttemptAt, (DateTime?)null), cancellationToken);

        var state = await dbContext.GenreMasterStates.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (state is null)
        {
            state = new GenreMasterState { Id = 1 };
            dbContext.GenreMasterStates.Add(state);
        }
        state.LastUpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "ジャンルマスターを更新しました: Added={Added}, Updated={Updated}, Deactivated={Deactivated}, Reactivated={Reactivated}",
            added,
            updatedIds.Count,
            deactivated,
            reactivated);

        return new GenreMasterUpdateResult(added, updatedIds.Count, deactivated, reactivated, scraped.Count, now);
    }

    private static void Validate(IReadOnlyList<ScrapedGenre> genres)
    {
        if (genres.Count == 0) throw new InvalidOperationException("ジャンルマスターを1件も解析できなかった");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            if (string.IsNullOrWhiteSpace(genre.ExternalId)) throw new InvalidOperationException("ジャンル外部IDが空である");
            if (string.IsNullOrWhiteSpace(genre.Name)) throw new InvalidOperationException($"ジャンル名が空である: {genre.ExternalId}");
            if (!ids.Add(genre.ExternalId)) throw new InvalidOperationException($"ジャンル外部IDが重複している: {genre.ExternalId}");
        }

        var byId = genres.ToDictionary(x => x.ExternalId, StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            if (genre.ParentExternalId is not null && !ids.Contains(genre.ParentExternalId))
                throw new InvalidOperationException($"親ジャンルが存在しない: {genre.ExternalId} -> {genre.ParentExternalId}");

            var visited = new HashSet<string>(StringComparer.Ordinal) { genre.ExternalId };
            var current = genre;
            while (current.ParentExternalId is not null)
            {
                if (!visited.Add(current.ParentExternalId))
                    throw new InvalidOperationException($"ジャンル階層に循環がある: {genre.ExternalId}");
                current = byId[current.ParentExternalId];
            }
        }
    }
}

/// <summary>ジャンルマスター更新結果を保持する</summary>
public sealed record GenreMasterUpdateResult(
    int Added,
    int Updated,
    int Deactivated,
    int Reactivated,
    int Total,
    DateTime UpdatedAt);

/// <summary>現在のジャンルマスター状態を保持する</summary>
public sealed record GenreMasterStatus(int ActiveCount, int InactiveCount, DateTime? LastUpdatedAt);
