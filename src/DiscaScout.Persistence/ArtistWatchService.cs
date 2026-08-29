using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// アーティスト設定の保存と、既存CDに対する一致状態の再評価を行う
/// </summary>
public sealed class ArtistWatchService(DiscaScoutDbContext dbContext, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 新しいアーティスト設定を作成し、既存CDとの一致状態を初期化する
    /// </summary>
    /// <param name="artist">表示用のアーティスト名</param>
    /// <param name="matchType">アーティスト一致方法</param>
    /// <param name="isWatchEnabled">新着Watchを有効にするか</param>
    /// <param name="collectFullCatalog">全作品収集を有効にするか。収集処理自体は別機能で行う</param>
    /// <param name="reopenExistingReviewedMatches">既存の一致済みCDを未チェックへ戻すか</param>
    public async Task<ArtistSetting> CreateAsync(
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        var setting = new ArtistSetting
        {
            Artist = artist.Trim(),
            NormalizedArtist = DiscTextNormalizer.Normalize(artist),
            MatchType = matchType,
            IsWatchEnabled = isWatchEnabled,
            CollectFullCatalog = collectFullCatalog
        };

        dbContext.ArtistSettings.Add(setting);
        await dbContext.SaveChangesAsync(cancellationToken);
        await ReevaluateAsync(setting.Id, reopenExistingReviewedMatches, cancellationToken);
        return setting;
    }

    /// <summary>
    /// アーティスト設定を更新し、変更後の条件で既存CDを再評価する
    /// </summary>
    /// <param name="artistSettingId">更新対象の設定ID</param>
    /// <param name="artist">表示用のアーティスト名</param>
    /// <param name="matchType">アーティスト一致方法</param>
    /// <param name="isWatchEnabled">新着Watchを有効にするか</param>
    /// <param name="collectFullCatalog">全作品収集を有効にするか</param>
    /// <param name="reopenExistingReviewedMatches">新たに一致した既存CDを未チェックへ戻すか</param>
    public async Task UpdateAsync(
        long artistSettingId,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        var setting = await dbContext.ArtistSettings
            .SingleAsync(x => x.Id == artistSettingId, cancellationToken);
        setting.Artist = artist.Trim();
        setting.NormalizedArtist = DiscTextNormalizer.Normalize(artist);
        setting.MatchType = matchType;
        setting.IsWatchEnabled = isWatchEnabled;
        setting.CollectFullCatalog = collectFullCatalog;

        await dbContext.SaveChangesAsync(cancellationToken);
        await ReevaluateAsync(setting.Id, reopenExistingReviewedMatches, cancellationToken);
    }

    /// <summary>
    /// アーティスト設定をアーカイブまたは復元する
    /// </summary>
    /// <remarks>
    /// アーカイブ時はWatch/全作品収集フラグや一致履歴を変更しない。復元時は、アーカイブ中に
    /// CDのArtist表記が変化している可能性があるため、ローカルCDとの一致状態だけ再評価する。
    /// </remarks>
    public async Task SetArchivedAsync(
        long artistSettingId,
        bool isArchived,
        bool reopenExistingReviewedMatches = false,
        CancellationToken cancellationToken = default)
    {
        var setting = await dbContext.ArtistSettings
            .SingleAsync(x => x.Id == artistSettingId, cancellationToken);
        setting.IsArchived = isArchived;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (!isArchived)
        {
            await ReevaluateAsync(setting.Id, reopenExistingReviewedMatches, cancellationToken);
        }
    }

    /// <summary>
    /// 指定した設定条件で全CDの現在一致状態を再評価する
    /// </summary>
    /// <remarks>
    /// 設定追加・編集時には過去の一致状態も維持する。既存CDをInboxへ戻すかどうかは
    /// 利用者の判断事項なので、呼び出し側から明示的に指定する。
    /// </remarks>
    public async Task ReevaluateAsync(
        long artistSettingId,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken = default)
    {
        var setting = await dbContext.ArtistSettings
            .SingleAsync(x => x.Id == artistSettingId, cancellationToken);
        var discs = await dbContext.Discs
            .Include(x => x.ArtistMatches)
            .Include(x => x.ReviewReasons)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();

        foreach (var disc in discs)
        {
            var shouldMatch = ArtistWatchMatcher.IsMatch(disc.NormalizedArtist, setting);
            var relation = disc.ArtistMatches.SingleOrDefault(x => x.ArtistSettingId == setting.Id);

            if (shouldMatch)
            {
                if (relation is null)
                {
                    relation = new DiscArtistMatch
                    {
                        ArtistSetting = setting,
                        IsCurrentMatch = true,
                        FirstMatchedAt = now,
                        LastMatchedAt = now
                    };
                    disc.ArtistMatches.Add(relation);

                    if (!setting.IsArchived && setting.IsWatchEnabled && reopenExistingReviewedMatches && !disc.IsRented)
                    {
                        AddArtistMatchedReason(disc, now);
                    }
                }
                else if (!relation.IsCurrentMatch)
                {
                    relation.IsCurrentMatch = true;
                    relation.LastMatchedAt = now;
                    relation.LastUnmatchedAt = null;

                    if (!setting.IsArchived && setting.IsWatchEnabled && reopenExistingReviewedMatches && !disc.IsRented)
                    {
                        AddArtistMatchedReason(disc, now);
                    }
                }
            }
            else if (relation is { IsCurrentMatch: true })
            {
                relation.IsCurrentMatch = false;
                relation.LastUnmatchedAt = now;
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// 通常スクレイピングで取得したCDについて、現在有効なWatch設定との一致状態を更新する
    /// </summary>
    /// <remarks>
    /// 同じ一致が継続しているだけでは再度ARTIST_MATCHEDを付与しない。
    /// Artist表記変更などで新たに一致した場合だけInboxを再オープンする。
    /// </remarks>
    internal static bool ApplyCurrentMatches(
        Disc disc,
        IReadOnlyCollection<ArtistSetting> settings,
        DateTimeOffset now)
    {
        var changed = false;

        foreach (var setting in settings.Where(x => !x.IsArchived))
        {
            var shouldMatch = ArtistWatchMatcher.IsMatch(disc.NormalizedArtist, setting);
            var relation = disc.ArtistMatches.SingleOrDefault(x => x.ArtistSettingId == setting.Id);

            if (shouldMatch && relation is null)
            {
                disc.ArtistMatches.Add(new DiscArtistMatch
                {
                    ArtistSetting = setting,
                    IsCurrentMatch = true,
                    FirstMatchedAt = now,
                    LastMatchedAt = now
                });
                changed = true;

                if (setting.IsWatchEnabled && !disc.IsRented)
                {
                    AddArtistMatchedReason(disc, now);
                }
            }
            else if (shouldMatch && relation is { IsCurrentMatch: false })
            {
                relation.IsCurrentMatch = true;
                relation.LastMatchedAt = now;
                relation.LastUnmatchedAt = null;
                changed = true;

                if (setting.IsWatchEnabled && !disc.IsRented)
                {
                    AddArtistMatchedReason(disc, now);
                }
            }
            else if (!shouldMatch && relation is { IsCurrentMatch: true })
            {
                relation.IsCurrentMatch = false;
                relation.LastUnmatchedAt = now;
                changed = true;
            }
        }

        return changed;
    }

    private static void AddArtistMatchedReason(Disc disc, DateTimeOffset now)
    {
        if (disc.ReviewReasons.All(x => x.Reason != DiscReviewReasonType.ArtistMatched))
        {
            disc.ReviewReasons.Add(new DiscReviewReason
            {
                Reason = DiscReviewReasonType.ArtistMatched,
                CreatedAt = now
            });
        }

        disc.NeedsReview = true;
    }
}
