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
    /// <param name="cancellationToken">DB反映処理を中断するためのトークン</param>
    /// <returns>今回の反映で新規追加・更新・非アクティブ化した件数</returns>
    public async Task<SnapshotApplyResult> ApplyAsync(
        DiscasCategorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = clock.GetUtcNow();
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

        foreach (var scraped in snapshot.Products)
        {
            seenIds.Add(scraped.DiscasId);

            if (!byDiscasId.TryGetValue(scraped.DiscasId, out var disc))
            {
                disc = CreateDisc(scraped, now);
                disc.Sources.Add(CreateSource(category, scraped.SourceRank, now));
                AddReviewReason(disc, DiscReviewReasonType.New, now);
                ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);
                dbContext.Discs.Add(disc);
                discs.Add(disc);
                byDiscasId.Add(disc.DiscasId, disc);
                added++;
                continue;
            }

            var wasArchived = disc.IsArchived;
            var changed = ApplyMetadata(disc, scraped, now);

            // Artist表記が変わった場合を含め、現在の正規化済みArtistに対してWatchを再評価する。
            // 継続一致では状態を変更しないため、週次クロールのたびにInboxが再オープンすることはない。
            changed |= ArtistWatchService.ApplyCurrentMatches(disc, artistSettings, now);

            var source = disc.Sources.SingleOrDefault(x => x.Category == category);
            if (source is null)
            {
                source = CreateSource(category, scraped.SourceRank, now);
                disc.Sources.Add(source);
                changed = true;
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

            if (wasArchived && !disc.IsRented)
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

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SnapshotApplyResult(added, updated, deactivated);
    }

    private static Disc CreateDisc(ScrapedDisc scraped, DateTimeOffset now)
    {
        var disc = new Disc
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
            NeedsReview = true
        };

        return disc;
    }

    private static DiscSource CreateSource(DiscReleaseCategory category, int sourceRank, DateTimeOffset now)
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

    private static bool ApplyMetadata(Disc disc, ScrapedDisc scraped, DateTimeOffset now)
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

        if (disc.RentalStartDate != scraped.RentalStartDate)
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

    private static void AddReviewReason(Disc disc, DiscReviewReasonType reason, DateTimeOffset now)
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

    private static void AddHistory(Disc disc, string field, string? oldValue, string? newValue, DateTimeOffset now)
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
}

/// <summary>
/// 1カテゴリのスナップショット反映結果を保持する
/// </summary>
/// <param name="AddedCount">新規作成したCD数</param>
/// <param name="UpdatedCount">既存CDでメタデータまたはソース状態を更新した数</param>
/// <param name="DeactivatedSourceCount">2回連続で消失しInactiveへ移したソース数</param>
public sealed record SnapshotApplyResult(int AddedCount, int UpdatedCount, int DeactivatedSourceCount);
