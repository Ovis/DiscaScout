using DiscaScout.Core;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// 正常取得済みのDISCASカテゴリスナップショットを現在状態と履歴へ反映する
/// </summary>
public sealed class DiscasSnapshotApplier(DiscaScoutDbContext dbContext, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 完全性検証済みのカテゴリスナップショットを1トランザクションで反映する
    /// </summary>
    /// <param name="snapshot">全ページ取得と整合性検証に成功したカテゴリスナップショット</param>
    /// <param name="consumeCountDropOverride">今回の反映と同一トランザクションで急減許可を消費する場合はtrue</param>
    /// <param name="cancellationToken">DB反映処理を中断するためのトークン</param>
    /// <returns>今回の反映で新規追加・更新・非アクティブ化した件数</returns>
    public async Task<SnapshotApplyResult> ApplyAsync(
        DiscasCategorySnapshot snapshot,
        bool consumeCountDropOverride = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // 永続タイムスタンプはSQLiteで比較・ソート可能なUTC DateTimeへ統一する。
        var now = clock.GetUtcNow().UtcDateTime;
        var category = MapCategory(snapshot.Category);

        // カテゴリ単位の完全スナップショットとして反映するため、途中で失敗した場合は
        // 新規追加だけ残るような状態を避けて全変更をロールバックする。
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var artistSettings = await dbContext.ArtistSettings
            .Where(x => !x.IsArchived)
            .ToListAsync(cancellationToken);
        var discs = await dbContext.Discs
            .Include(x => x.Sources)
            .Include(x => x.ReviewReasons)
            .Include(x => x.ChangeHistory)
            .Include(x => x.ArtistMatches)
            .ToListAsync(cancellationToken);
        var byDiscasId = discs.ToDictionary(x => x.DiscasId, StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        var added = 0;
        var updated = 0;
        var artistWatchNewMatches = 0;

        foreach (var scraped in snapshot.Products)
        {
            seenIds.Add(scraped.DiscasId);

            if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
            {
                disc = CreateDisc(scraped, now);
                disc.Sources.Add(CreateSource(category, scraped.SourceRank, now));
                AddReviewReason(disc, DiscReviewReasonType.New, now);
                var hadArtistMatchedReason = HasArtistMatchedReason(disc);
                ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);
                if (!hadArtistMatchedReason && HasArtistMatchedReason(disc))
                {
                    // 通知上の件数はWatch設定数ではなく、今回新たにARTIST_MATCHEDとなったCD数として数える。
                    // 複数のWatch設定に同じCDが一致してもユーザーが確認するCDは1件なので重複計上しない。
                    artistWatchNewMatches++;
                }

                dbContext.Discs.Add(disc);
                discs.Add(disc);
                byDiscasId.Add(disc.DiscasId, disc);
                added++;
                continue;
            }

            var hadNormalSource = disc.Sources.Count > 0;
            var wasArchived = disc.IsArchived;
            var changed = ApplyMetadata(disc, scraped, now);

            // Artist表記が変わった場合を含め、現在の正規化済みArtistに対してWatchを再評価する。
            // 継続一致では状態を変更しないため、週次クロールのたびにInboxが再オープンすることはない。
            var hadArtistMatchedReason = HasArtistMatchedReason(disc);
            changed |= ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);
            if (!hadArtistMatchedReason && HasArtistMatchedReason(disc))
            {
                artistWatchNewMatches++;
            }

            var source = disc.Sources.SingleOrDefault(x => x.Category == category);
            if (source is null)
            {
                source = CreateSource(category, scraped.SourceRank, now);
                disc.Sources.Add(source);
                changed = true;

                // 全作品収集で先にDBへ入ったCDには通常Sourceが1件もない。
                // そのCDが初めてNew/Upcomingへ現れた時点をユーザーにとっての「新着」として扱う。
                if (!hadNormalSource && !disc.IsRented)
                {
                    AddReviewReason(disc, DiscReviewReasonType.New, now);
                    disc.NeedsReview = true;
                }
            }
            else
            {
                if (!source.IsActive || source.MissingCount != 0 || source.SourceRank != scraped.SourceRank)
                {
                    changed = true;
                }

                source.IsActive = true;
                source.MissingCount = 0;
                source.SourceRank = scraped.SourceRank;
                source.LastSeenAt = now;
            }

            disc.LastSeenAt = now;
            disc.IsArchived = false;

            // Catalog専用CDはIsArchived=trueで保持するが、通常Sourceへの初参加は「再出現」ではない。
            // 過去に通常カテゴリで観測済みだったCDだけREAPPEAREDを付与する。
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
                // 一時的な検索揺れやDISCAS側の反映遅延で即座にArchiveしないよう、
                // 同一カテゴリの正常クロールで2回連続して消えた場合にのみInactiveへ移す。
                source.MissingCount++;
                if (source.MissingCount >= 2)
                {
                    source.IsActive = false;
                    deactivated++;
                }
            }

            disc.IsArchived = !disc.Sources.Any(x => x.IsActive);
        }

        if (consumeCountDropOverride)
        {
            // スナップショットだけコミットされてOverride消費に失敗すると、RetryでMissingCountを二重加算し得る。
            // そのためOverrideの消費も同じトランザクションへ含め、両方が成功するか両方ともロールバックする。
            var scrapeCategory = MapScrapeCategory(snapshot.Category);
            var guard = await dbContext.ScrapeGuardSettings
                .SingleOrDefaultAsync(x => x.Category == scrapeCategory, cancellationToken);
            if (guard is null || !guard.IsCountDropOverrideEnabled)
            {
                throw new InvalidOperationException($"急減許可が有効ではないためスナップショットを反映できない: {scrapeCategory}");
            }

            guard.IsCountDropOverrideEnabled = false;
            guard.CountDropOverrideEnabledAt = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SnapshotApplyResult(added, updated, deactivated)
        {
            ArtistWatchNewMatchCount = artistWatchNewMatches
        };
    }

    private static Disc CreateDisc(ScrapedDisc scraped, DateTime now)
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
            IsMaxiSingle = scraped.IsMaxiSingle,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            NeedsReview = true
        };
    }

    private static DiscSource CreateSource(DiscReleaseCategory category, int sourceRank, DateTime now)
    {
        return new DiscSource
        {
            Category = category,
            SourceRank = sourceRank,
            IsActive = true,
            MissingCount = 0,
            LastSeenAt = now
        };
    }

    private static bool ApplyMetadata(Disc disc, ScrapedDisc scraped, DateTime now)
    {
        var changed = false;
        var normalizedTitle = DiscTextNormalizer.Normalize(scraped.Title);
        var normalizedArtist = DiscTextNormalizer.Normalize(scraped.Artist);

        if (!string.Equals(disc.NormalizedTitle, normalizedTitle, StringComparison.Ordinal))
        {
            AddHistory(disc, nameof(Disc.Title), disc.Title, scraped.Title, now);
            if (!disc.IsRented)
            {
                AddReviewReason(disc, DiscReviewReasonType.TitleChanged, now);
                disc.NeedsReview = true;
            }

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
        changed |= AssignIfChanged(disc.GenreLarge, scraped.GenreLarge, x => disc.GenreLarge = x);
        changed |= AssignIfChanged(disc.GenreMiddle, scraped.GenreMiddle, x => disc.GenreMiddle = x);
        changed |= AssignIfChanged(disc.GenreSmall, scraped.GenreSmall, x => disc.GenreSmall = x);
        changed |= AssignIfChanged(disc.ImageUrl, scraped.ImageUrl, x => disc.ImageUrl = x);
        changed |= AssignIfChanged(disc.IsMaxiSingle, scraped.IsMaxiSingle, x => disc.IsMaxiSingle = x);

        // RentalStartDateは一覧HTMLでは取得できないため、詳細ページで取得済みの値を
        // nullで上書きしない。将来一覧側から値を取得できるようになった場合だけ更新する。
        if (scraped.RentalStartDate is not null && disc.RentalStartDate != scraped.RentalStartDate)
        {
            disc.RentalStartDate = scraped.RentalStartDate;
            changed = true;
        }

        return changed;
    }

    private static bool AssignIfChanged<T>(T current, T next, Action<T> assign)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
        {
            return false;
        }

        assign(next);
        return true;
    }

    private static bool HasArtistMatchedReason(Disc disc)
    {
        return disc.ReviewReasons.Any(x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    private static void AddReviewReason(Disc disc, DiscReviewReasonType reason, DateTime now)
    {
        if (disc.ReviewReasons.Any(x => x.Reason == reason))
        {
            return;
        }

        disc.ReviewReasons.Add(new DiscReviewReason
        {
            Reason = reason,
            CreatedAt = now
        });
    }

    private static void AddHistory(Disc disc, string field, string? oldValue, string? newValue, DateTime now)
    {
        disc.ChangeHistory.Add(new DiscChangeHistory
        {
            Field = field,
            OldValue = oldValue,
            NewValue = newValue,
            ChangedAt = now
        });
    }

    private static DiscReleaseCategory MapCategory(DiscSourceCategory category)
    {
        return category switch
        {
            DiscSourceCategory.Upcoming => DiscReleaseCategory.Upcoming,
            DiscSourceCategory.New => DiscReleaseCategory.New,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
    }

    private static ScrapeCategory MapScrapeCategory(DiscSourceCategory category)
    {
        return category switch
        {
            DiscSourceCategory.Upcoming => ScrapeCategory.Upcoming,
            DiscSourceCategory.New => ScrapeCategory.New,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
        };
    }
}

/// <summary>
/// 1カテゴリのスナップショット反映結果を保持する
/// </summary>
/// <param name="AddedCount">新規作成したCD数</param>
/// <param name="UpdatedCount">既存CDでメタデータまたはソース状態を更新した数</param>
/// <param name="DeactivatedSourceCount">2回連続で消失しInactiveへ移したソース数</param>
public sealed record SnapshotApplyResult(int AddedCount, int UpdatedCount, int DeactivatedSourceCount)
{
    /// <summary>
    /// 今回の通常スナップショット反映で新たにArtist Watchへ一致し、ARTIST_MATCHED理由が付与されたCD数
    /// </summary>
    public int ArtistWatchNewMatchCount { get; init; }
}
