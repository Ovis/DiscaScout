using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscaScout.Persistence;

/// <summary>
/// Artist全作品収集の設定参照と完全スナップショット反映を行う
/// </summary>
public sealed class ArtistCatalogStore(
    DiscaScoutDbContext dbContext,
    GenreResolver genreResolver,
    TimeProvider? timeProvider = null,
    ILogger<ArtistCatalogStore>? logger = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<ArtistCatalogStore> logger = logger ?? NullLogger<ArtistCatalogStore>.Instance;

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

            var applyResult = await ApplyDiscAsync(scraped, byDiscasId, now, reviewInitialItems, cancellationToken);
            if (applyResult.Added) addedDiscCount++;

            var disc = applyResult.Disc;
            if (!applyResult.Added) seenDiscIds.Add(disc.Id);
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

    /// <summary>
    /// Artist設定を作成せず、一回限りのアーティスト検索結果をCDへ取り込む
    /// </summary>
    /// <param name="artist">検索条件として指定したアーティスト名</param>
    /// <param name="matchType">検索結果のアーティスト表記に対する一致方法</param>
    /// <param name="snapshot">DISCASから取得した検索結果全体</param>
    /// <param name="reviewNewItems">今回新規登録したCDを未チェックとして保持するか</param>
    public async Task<OneShotArtistCatalogApplyResult> ApplyOneShotAsync(
        string artist,
        ArtistMatchType matchType,
        DiscasArtistCatalogSnapshot snapshot,
        bool reviewNewItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);
        ArgumentNullException.ThrowIfNull(snapshot);

        var normalizedTarget = DiscTextNormalizer.Normalize(artist);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var discs = await dbContext.Discs.Include(x => x.Sources).Include(x => x.ArtistCatalogEntries).ToListAsync(cancellationToken);
        var byDiscasId = discs.ToDictionary(x => x.DiscasId, StringComparer.Ordinal);
        var matchedCount = 0;
        var addedDiscCount = 0;

        logger.LogInformation(
            "一回限りArtist一致判定を開始します。Target={Target}, NormalizedTarget={NormalizedTarget}, MatchType={MatchType}, SearchResultCount={SearchResultCount}",
            artist,
            normalizedTarget,
            matchType,
            snapshot.Products.Count);

        foreach (var scraped in snapshot.Products)
        {
            var normalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);
            var matched = IsMatch(normalizedArtist, normalizedTarget, matchType);

            // K検索は汎用検索なので、検索結果からどのArtist文字列が抽出され、
            // 後段の一致判定で採用・除外されたかを診断できるよう一時的に全件を記録する。
            logger.LogInformation(
                "一回限りArtist一致判定: Title={Title}, ScrapedArtist={ScrapedArtist}, NormalizedArtist={NormalizedArtist}, Target={Target}, NormalizedTarget={NormalizedTarget}, MatchType={MatchType}, Matched={Matched}",
                scraped.Title,
                scraped.Artist,
                normalizedArtist,
                artist,
                normalizedTarget,
                matchType,
                matched);

            if (!matched) continue;
            matchedCount++;

            var applyResult = await ApplyDiscAsync(scraped, byDiscasId, now, reviewNewItems, cancellationToken);
            if (applyResult.Added) addedDiscCount++;
        }

        logger.LogInformation(
            "一回限りArtist一致判定が完了しました。Target={Target}, MatchType={MatchType}, SearchResultCount={SearchResultCount}, MatchedCount={MatchedCount}, AddedDiscCount={AddedDiscCount}",
            artist,
            matchType,
            snapshot.Products.Count,
            matchedCount,
            addedDiscCount);

        // 一回限り取得ではArtistSettingやCatalog relationを作らず、CDそのものだけを永続化する。
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OneShotArtistCatalogApplyResult(snapshot.TotalCount, matchedCount, addedDiscCount);
    }

    private async Task<DiscApplyResult> ApplyDiscAsync(
        ScrapedDisc scraped,
        Dictionary<string, Disc> byDiscasId,
        DateTime now,
        bool needsReviewForNewDisc,
        CancellationToken cancellationToken)
    {
        var genre = await genreResolver.ResolveAsync(scraped.GenreLarge, scraped.GenreMiddle, scraped.GenreSmall, cancellationToken);
        if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
        {
            disc = CreateCatalogOnlyDisc(scraped, genre?.Id, now, needsReviewForNewDisc);
            dbContext.Discs.Add(disc);
            byDiscasId.Add(disc.DiscasId, disc);
            return new DiscApplyResult(disc, true);
        }

        // 通常カテゴリ由来の情報をCatalog検索で上書きしない既存仕様を、一回限り取得でも維持する。
        if (disc.Sources.Count == 0) ApplyCatalogMetadata(disc, scraped, genre?.Id, now);
        return new DiscApplyResult(disc, false);
    }

    private static bool IsMatch(string normalizedArtist, string normalizedTarget, ArtistMatchType matchType) =>
        matchType switch
        {
            ArtistMatchType.Exact => string.Equals(normalizedArtist, normalizedTarget, StringComparison.Ordinal),
            ArtistMatchType.Contains => normalizedArtist.Contains(normalizedTarget, StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(matchType), matchType, null)
        };

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

    private sealed record DiscApplyResult(Disc Disc, bool Added);
}

/// <summary>Artist全作品スナップショットの反映結果を保持する</summary>
public sealed record ArtistCatalogApplyResult(int SearchResultCount, int MatchedCount, int AddedDiscCount, int ActivatedCount, int DeactivatedCount);

/// <summary>一回限りのArtist全作品取得結果を保持する</summary>
public sealed record OneShotArtistCatalogApplyResult(int SearchResultCount, int MatchedCount, int AddedDiscCount);
