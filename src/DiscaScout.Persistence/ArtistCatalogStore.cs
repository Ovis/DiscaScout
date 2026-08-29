using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// Artist全作品収集の設定参照と完全スナップショット反映を行う
/// </summary>
public sealed class ArtistCatalogStore(DiscaScoutDbContext dbContext, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 全作品収集対象のアーティスト設定を取得する
    /// </summary>
    /// <param name="artistSettingId">取得対象のArtistSetting ID</param>
    /// <param name="cancellationToken">DBアクセスを中断するためのトークン</param>
    /// <returns>存在する場合は設定、存在しない場合はnull</returns>
    public Task<ArtistSetting?> FindSettingAsync(
        long artistSettingId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.ArtistSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == artistSettingId, cancellationToken);
    }

    /// <summary>
    /// 正常取得済みのアーティスト検索結果を専用Catalog関係へ反映する
    /// </summary>
    /// <remarks>
    /// DISCASのアーティスト検索は作曲・参加作品など対象Artist以外の商品も返し得るため、
    /// 検索結果全体をそのまま採用せずArtistSettingのExact/Contains条件で後段フィルタする。
    /// Catalog単独で初めて発見したCDはInboxへ出さず、通常New/Upcomingに現れた時点で初めてNEW扱いとする。
    /// </remarks>
    public async Task<ArtistCatalogApplyResult> ApplyAsync(
        long artistSettingId,
        DiscasArtistCatalogSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var setting = await dbContext.ArtistSettings
            .SingleAsync(x => x.Id == artistSettingId, cancellationToken);
        if (setting.IsArchived || !setting.CollectFullCatalog)
        {
            throw new InvalidOperationException("全作品収集が有効なArtistSettingではない");
        }

        var now = clock.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var discs = await dbContext.Discs
            .Include(x => x.Sources)
            .Include(x => x.ArtistCatalogEntries)
            .ToListAsync(cancellationToken);
        var byDiscasId = discs.ToDictionary(x => x.DiscasId, StringComparer.Ordinal);
        var existingRelations = await dbContext.DiscArtistCatalogs
            .Where(x => x.ArtistSettingId == artistSettingId)
            .ToListAsync(cancellationToken);
        var relationByDiscId = existingRelations.ToDictionary(x => x.DiscId);
        var seenDiscIds = new HashSet<long>();

        var matchedCount = 0;
        var addedDiscCount = 0;
        var activatedCount = 0;

        foreach (var scraped in snapshot.Products)
        {
            var normalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
            if (!ArtistWatchMatcher.IsMatch(normalizedArtist, setting))
            {
                continue;
            }

            matchedCount++;
            if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
            {
                disc = CreateCatalogOnlyDisc(scraped, now);
                dbContext.Discs.Add(disc);
                discs.Add(disc);
                byDiscasId.Add(disc.DiscasId, disc);
                addedDiscCount++;

                // IDはSaveChangesまで確定しないため、新規DiscのCatalog relationはnavigation経由で追加する。
                var relation = CreateRelation(setting, now);
                disc.ArtistCatalogEntries.Add(relation);
                activatedCount++;
                continue;
            }

            seenDiscIds.Add(disc.Id);

            // Catalogだけで保持しているCDは再取得時に表示情報を更新する。
            // 通常カテゴリで観測済みのCDはInbox差分判定をCatalog取得で先食いしないよう更新しない。
            if (disc.Sources.Count == 0)
            {
                ApplyCatalogMetadata(disc, scraped, now);
            }

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

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ArtistCatalogApplyResult(
            snapshot.TotalCount,
            matchedCount,
            addedDiscCount,
            activatedCount,
            deactivatedCount);
    }

    private static Disc CreateCatalogOnlyDisc(ScrapedDisc scraped, DateTimeOffset now)
    {
        return new Disc
        {
            DiscasId = scraped.DiscasId,
            ProductUrl = scraped.ProductUrl,
            Title = scraped.Title,
            NormalizedTitle = DiscTextNormalizer.Normalize(scraped.Title),
            Artist = scraped.Artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist),
            GenreLarge = scraped.GenreLarge,
            GenreMiddle = scraped.GenreMiddle,
            GenreSmall = scraped.GenreSmall,
            ImageUrl = scraped.ImageUrl,
            RentalStartDate = scraped.RentalStartDate,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            IsArchived = true,
            NeedsReview = false
        };
    }

    private static DiscArtistCatalog CreateRelation(ArtistSetting setting, DateTimeOffset now)
    {
        return new DiscArtistCatalog
        {
            ArtistSetting = setting,
            IsActive = true,
            FirstSeenAt = now,
            LastSeenAt = now
        };
    }

    private static void ApplyCatalogMetadata(Disc disc, ScrapedDisc scraped, DateTimeOffset now)
    {
        disc.ProductUrl = scraped.ProductUrl;
        disc.Title = scraped.Title;
        disc.NormalizedTitle = DiscTextNormalizer.Normalize(scraped.Title);
        disc.Artist = scraped.Artist;
        disc.NormalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
        disc.GenreLarge = scraped.GenreLarge;
        disc.GenreMiddle = scraped.GenreMiddle;
        disc.GenreSmall = scraped.GenreSmall;
        disc.ImageUrl = scraped.ImageUrl;
        disc.RentalStartDate = scraped.RentalStartDate;
        disc.LastSeenAt = now;
        disc.LastUpdatedAt = now;
    }
}

/// <summary>
/// Artist全作品スナップショットの反映結果を保持する
/// </summary>
/// <param name="SearchResultCount">DISCAS検索結果全体の件数</param>
/// <param name="MatchedCount">ArtistSetting条件で後段フィルタ後に採用した件数</param>
/// <param name="AddedDiscCount">Catalog専用CDとして新規作成した件数</param>
/// <param name="ActivatedCount">新規または再有効化したCatalog関係数</param>
/// <param name="DeactivatedCount">今回の正常取得で消失しInactiveへ移したCatalog関係数</param>
public sealed record ArtistCatalogApplyResult(
    int SearchResultCount,
    int MatchedCount,
    int AddedDiscCount,
    int ActivatedCount,
    int DeactivatedCount);
