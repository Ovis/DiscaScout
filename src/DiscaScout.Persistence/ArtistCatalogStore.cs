using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// Artist全作品収集の設定参照と完全スナップショット反映を行う
/// </summary>
public sealed class ArtistCatalogStore(
    DiscaScoutDbContext dbContext,
    GenreResolver genreResolver,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>テスト用に時刻プロバイダーだけを指定して初期化する</summary>
    public ArtistCatalogStore(DiscaScoutDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, new GenreResolver(dbContext), timeProvider)
    {
    }

    /// <summary>全作品収集対象のアーティスト設定を取得する</summary>
    public Task<ArtistSetting?> FindSettingAsync(long artistSettingId, CancellationToken cancellationToken = default) =>
        dbContext.ArtistSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == artistSettingId, cancellationToken);

    /// <summary>正常取得済みのアーティスト検索結果を専用Catalog関係へ反映する</summary>
    public async Task<ArtistCatalogApplyResult> ApplyAsync(long artistSettingId, DiscasArtistCatalogSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var setting = await dbContext.ArtistSettings.SingleAsync(x => x.Id == artistSettingId, cancellationToken);
        if (setting.IsArchived || !setting.CollectFullCatalog) throw new InvalidOperationException("全作品収集が有効なArtistSettingではない");

        var now = clock.GetUtcNow().UtcDateTime;
        var isInitialCollection = !setting.InitialCatalogCollectionCompleted;
        var reviewInitialItems = isInitialCollection && setting.ReviewInitialCatalogItems;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var discs = await dbContext.Discs.Include(x => x.Sources).Include(x => x.ArtistCatalogEntries).ToListAsync(cancellationToken);
        var byDiscasId = discs.ToDictionary(x => x.DiscasId, StringComparer.Ordinal);
        var existingRelations = await dbContext.DiscArtistCatalogs.Where(x => x.ArtistSettingId == artistSettingId).ToListAsync(cancellationToken);
        var relationByDiscId = existingRelations.ToDictionary(x => x.DiscId);
        var seenDiscIds = new HashSet<long>();
        var matchedCount = 0;
        var addedDiscCount = 0;
        var activatedCount = 0;

        foreach (var scraped in snapshot.Products)
        {
            var normalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
            if (!ArtistWatchMatcher.IsMatch(normalizedArtist, setting)) continue;
            matchedCount++;
            var genre = await genreResolver.ResolveAsync(scraped.GenreLarge, scraped.GenreMiddle, scraped.GenreSmall, cancellationToken);

            if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
            {
                disc = CreateCatalogOnlyDisc(scraped, genre?.Id, now, reviewInitialItems);
                dbContext.Discs.Add(disc);
                discs.Add(disc);
                byDiscasId.Add(disc.DiscasId, disc);
                addedDiscCount++;
                disc.ArtistCatalogEntries.Add(CreateRelation(setting, now));
                activatedCount++;
                continue;
            }

            seenDiscIds.Add(disc.Id);
            if (disc.Sources.Count == 0) ApplyCatalogMetadata(disc, scraped, genre?.Id, now);
            if (!relationByDiscId.TryGetValue(disc.Id, out var existingRelation))
            {
                disc.ArtistCatalogEntries.Add(CreateRelation(setting, now));
                activatedCount++;
            }
            else
            {
                existingRelation.LastSeenAt = now;
                if (!existingRelation.IsActive)
                {
                    existingRelation.IsActive = true;
                    existingRelation.DeactivatedAt = null;
                    activatedCount++;
                }
            }
        }

        var deactivatedCount = 0;
        foreach (var relation in existingRelations.Where(x => x.IsActive && !seenDiscIds.Contains(x.DiscId)))
        {
            relation.IsActive = false;
            relation.DeactivatedAt = now;
            deactivatedCount++;
        }

        setting.InitialCatalogCollectionCompleted = true;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ArtistCatalogApplyResult(snapshot.TotalCount, matchedCount, addedDiscCount, activatedCount, deactivatedCount);
    }

    private static Disc CreateCatalogOnlyDisc(ScrapedDisc scraped, long? genreId, DateTime now, bool needsReview)
    {
        var disc = new Disc
        {
            DiscasId = scraped.DiscasId,
            ProductUrl = scraped.ProductUrl,
            Title = scraped.Title,
            NormalizedTitle = DiscTextNormalizer.Normalize(scraped.Title),
            Artist = scraped.Artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist),
            GenreId = genreId,
            ImageUrl = scraped.ImageUrl,
            RentalStartDate = scraped.RentalStartDate,
            IsMaxiSingle = scraped.IsMaxiSingle,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            IsArchived = true,
            NeedsReview = needsReview
        };
        if (needsReview) disc.ReviewReasons.Add(new DiscReviewReason { Reason = DiscReviewReasonType.ArtistMatched, CreatedAt = now });
        return disc;
    }

    private static DiscArtistCatalog CreateRelation(ArtistSetting setting, DateTime now) => new()
    {
        ArtistSetting = setting, IsActive = true, FirstSeenAt = now, LastSeenAt = now
    };

    private static void ApplyCatalogMetadata(Disc disc, ScrapedDisc scraped, long? genreId, DateTime now)
    {
        disc.ProductUrl = scraped.ProductUrl;
        disc.Title = scraped.Title;
        disc.NormalizedTitle = DiscTextNormalizer.Normalize(scraped.Title);
        disc.Artist = scraped.Artist;
        disc.NormalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
        disc.GenreId = genreId;
        disc.ImageUrl = scraped.ImageUrl;
        disc.IsMaxiSingle = scraped.IsMaxiSingle;
        if (scraped.RentalStartDate is not null) disc.RentalStartDate = scraped.RentalStartDate;
        disc.LastSeenAt = now;
        disc.LastUpdatedAt = now;
    }
}

/// <summary>Artist全作品スナップショットの反映結果を保持する</summary>
public sealed record ArtistCatalogApplyResult(int SearchResultCount, int MatchedCount, int AddedDiscCount, int ActivatedCount, int DeactivatedCount);
