using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscaScout.Persistence;

/// <summary>
/// 正常取得済みのDISCASカテゴリスナップショットを現在状態と履歴へ反映する
/// </summary>
public sealed class DiscasSnapshotApplier(
    DiscaScoutDbContext dbContext,
    GenreResolver genreResolver,
    ILogger<DiscasSnapshotApplier>? logger = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>既定時刻で初期化する互換コンストラクター</summary>
    public DiscasSnapshotApplier(DiscaScoutDbContext dbContext)
        : this(dbContext, new GenreResolver(dbContext), null, null)
    {
    }

    /// <summary>テスト用に時刻プロバイダーだけを指定して初期化する</summary>
    public DiscasSnapshotApplier(DiscaScoutDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, new GenreResolver(dbContext), null, timeProvider)
    {
    }

    /// <summary>完全性検証済みのカテゴリスナップショットを1トランザクションで反映する</summary>
    public async Task<SnapshotApplyResult> ApplyAsync(DiscasCategorySnapshot snapshot, bool consumeCountDropOverride = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = clock.GetUtcNow().UtcDateTime;
        var category = MapCategory(snapshot.Category);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var artistSettings = await dbContext.ArtistSettings.Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var discs = await dbContext.Discs.Include(x => x.Sources).Include(x => x.ReviewReasons).Include(x => x.ChangeHistory).Include(x => x.ArtistMatches).ToListAsync(cancellationToken);
        var byDiscasId = discs.ToDictionary(x => x.DiscasId, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var updated = 0;
        var artistWatchNewMatches = 0;

        foreach (var scraped in snapshot.Products)
        {
            seenIds.Add(scraped.DiscasId);
            var genre = await genreResolver.ResolveAsync(scraped.GenreLarge, scraped.GenreMiddle, scraped.GenreSmall, cancellationToken);
            if (genre is null)
            {
                logger?.LogWarning("検索結果のジャンルをマスターへ解決できませんでした: DiscasId={DiscasId}, Genre={GenreLarge} > {GenreMiddle} > {GenreSmall}", scraped.DiscasId, scraped.GenreLarge, scraped.GenreMiddle, scraped.GenreSmall);
            }

            if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
            {
                disc = CreateDisc(scraped, genre?.Id, now);
                disc.Sources.Add(CreateSource(category, scraped.SourceRank, now));
                AddReviewReason(disc, DiscReviewReasonType.New, now);
                ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);
                if (HasArtistMatchedReason(disc)) artistWatchNewMatches++;
                dbContext.Discs.Add(disc);
                discs.Add(disc);
                byDiscasId.Add(disc.DiscasId, disc);
                added++;
                continue;
            }

            var hadNormalSource = disc.Sources.Count > 0;
            var wasArchived = disc.IsArchived;
            var changed = ApplyMetadata(disc, scraped, genre?.Id, now);
            var hadArtistMatchedReason = HasArtistMatchedReason(disc);
            changed |= ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);
            if (!hadArtistMatchedReason && HasArtistMatchedReason(disc)) artistWatchNewMatches++;

            var source = disc.Sources.SingleOrDefault(x => x.Category == category);
            if (source is null)
            {
                source = CreateSource(category, scraped.SourceRank, now);
                disc.Sources.Add(source);
                changed = true;
                if (!hadNormalSource && !disc.IsRented)
                {
                    AddReviewReason(disc, DiscReviewReasonType.New, now);
                    disc.NeedsReview = true;
                }
            }
            else
            {
                if (!source.IsActive || source.MissingCount != 0 || source.SourceRank != scraped.SourceRank) changed = true;
                source.IsActive = true;
                source.MissingCount = 0;
                source.SourceRank = scraped.SourceRank;
                source.LastSeenAt = now;
            }

            disc.LastSeenAt = now;
            disc.IsArchived = false;
            if (wasArchived && hadNormalSource && !disc.IsRented)
            {
                AddReviewReason(disc, DiscReviewReasonType.Reappeared, now);
                disc.NeedsReview = true;
                changed = true;
            }
            if (changed)
            {
                disc.LastUpdatedAt = now;
                updated++;
            }
        }

        var deactivated = 0;
        foreach (var disc in discs)
        {
            var source = disc.Sources.SingleOrDefault(x => x.Category == category);
            if (source is not null && source.IsActive && !seenIds.Contains(disc.DiscasId))
            {
                source.MissingCount++;
                if (source.MissingCount >= 2)
                {
                    source.IsActive = false;
                    deactivated++;
                }
            }
            disc.IsArchived = !disc.Sources.Any(x => x.IsActive) && disc.RentalHistoryImportedAt is null;
        }

        if (consumeCountDropOverride)
        {
            var scrapeCategory = MapScrapeCategory(snapshot.Category);
            var guard = await dbContext.ScrapeGuardSettings.SingleOrDefaultAsync(x => x.Category == scrapeCategory, cancellationToken);
            if (guard is null || !guard.IsCountDropOverrideEnabled) throw new InvalidOperationException($"急減許可が有効ではないためスナップショットを反映できない: {scrapeCategory}");
            guard.IsCountDropOverrideEnabled = false;
            guard.CountDropOverrideEnabledAt = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SnapshotApplyResult(added, updated, deactivated) { ArtistWatchNewMatchCount = artistWatchNewMatches };
    }

    private static Disc CreateDisc(ScrapedDisc scraped, long? genreId, DateTime now) => new()
    {
        DiscasId = scraped.DiscasId, ProductUrl = scraped.ProductUrl, Title = scraped.Title,
        NormalizedTitle = DiscTextNormalizer.Normalize(scraped.Title), Artist = scraped.Artist,
        NormalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist), GenreId = genreId,
        ImageUrl = scraped.ImageUrl, RentalStartDate = scraped.RentalStartDate, IsMaxiSingle = scraped.IsMaxiSingle,
        FirstSeenAt = now, LastSeenAt = now, LastUpdatedAt = now, NeedsReview = true
    };

    private static DiscSource CreateSource(DiscReleaseCategory category, int sourceRank, DateTime now) => new()
    {
        Category = category, SourceRank = sourceRank, IsActive = true, MissingCount = 0, LastSeenAt = now
    };

    private static bool ApplyMetadata(Disc disc, ScrapedDisc scraped, long? genreId, DateTime now)
    {
        var changed = false;
        var normalizedTitle = DiscTextNormalizer.Normalize(scraped.Title);
        var normalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
        if (!string.Equals(disc.NormalizedTitle, normalizedTitle, StringComparison.Ordinal))
        {
            AddHistory(disc, nameof(Disc.Title), disc.Title, scraped.Title, now);
            if (!disc.IsRented) { AddReviewReason(disc, DiscReviewReasonType.TitleChanged, now); disc.NeedsReview = true; }
            changed = true;
        }
        if (!string.Equals(disc.NormalizedArtist, normalizedArtist, StringComparison.Ordinal))
        {
            AddHistory(disc, nameof(Disc.Artist), disc.Artist, scraped.Artist, now);
            changed = true;
        }
        changed |= AssignIfChanged(disc.ProductUrl, scraped.ProductUrl, x => disc.ProductUrl = x);
        changed |= AssignIfChanged(disc.Title, scraped.Title, x => disc.Title = x);
        changed |= AssignIfChanged(disc.NormalizedTitle, normalizedTitle, x => disc.NormalizedTitle = x);
        changed |= AssignIfChanged(disc.Artist, scraped.Artist, x => disc.Artist = x);
        changed |= AssignIfChanged(disc.NormalizedArtist, normalizedArtist, x => disc.NormalizedArtist = x);
        changed |= AssignIfChanged(disc.GenreId, genreId, x => disc.GenreId = x);
        changed |= AssignIfChanged(disc.ImageUrl, scraped.ImageUrl, x => disc.ImageUrl = x);
        changed |= AssignIfChanged(disc.IsMaxiSingle, scraped.IsMaxiSingle, x => disc.IsMaxiSingle = x);
        if (scraped.RentalStartDate is not null && disc.RentalStartDate != scraped.RentalStartDate) { disc.RentalStartDate = scraped.RentalStartDate; changed = true; }
        return changed;
    }

    private static bool AssignIfChanged<T>(T current, T next, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, next)) return false;
        assign(next); return true;
    }

    private static bool HasArtistMatchedReason(Disc disc) => disc.ReviewReasons.Any(x => x.Reason == DiscReviewReasonType.ArtistMatched);
    private static void AddReviewReason(Disc disc, DiscReviewReasonType reason, DateTime now)
    {
        if (!disc.ReviewReasons.Any(x => x.Reason == reason)) disc.ReviewReasons.Add(new DiscReviewReason { Reason = reason, CreatedAt = now });
    }
    private static void AddHistory(Disc disc, string field, string? oldValue, string? newValue, DateTime now) => disc.ChangeHistory.Add(new DiscChangeHistory { Field = field, OldValue = oldValue, NewValue = newValue, ChangedAt = now });
    private static DiscReleaseCategory MapCategory(DiscSourceCategory category) => category switch { DiscSourceCategory.Upcoming => DiscReleaseCategory.Upcoming, DiscSourceCategory.New => DiscReleaseCategory.New, _ => throw new ArgumentOutOfRangeException(nameof(category), category, null) };
    private static ScrapeCategory MapScrapeCategory(DiscSourceCategory category) => category switch { DiscSourceCategory.Upcoming => ScrapeCategory.Upcoming, DiscSourceCategory.New => ScrapeCategory.New, _ => throw new ArgumentOutOfRangeException(nameof(category), category, null) };
}

/// <summary>1カテゴリのスナップショット反映結果を保持する</summary>
public sealed record SnapshotApplyResult(int AddedCount, int UpdatedCount, int DeactivatedSourceCount)
{
    /// <summary>今回新たにArtist Watch一致となったCD数</summary>
    public int ArtistWatchNewMatchCount { get; init; }
}
