using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Application;

/// <summary>
/// ログイン済みブラウザから抽出したDISCASレンタル履歴を、レンタル済みCDとして取り込む
/// </summary>
public sealed class RentalHistoryImportService(DiscaScoutDbContext dbContext, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// レンタル履歴のCDを冪等に取り込み、詳細取得を優先させるCDの内部IDを返す
    /// </summary>
    /// <param name="entries">ブラウザから抽出したtitleID・タイトル・アーティスト</param>
    /// <param name="cancellationToken">DB更新処理を中断するためのトークン</param>
    public async Task<RentalHistoryImportResult> ImportAsync(
        IReadOnlyCollection<RentalHistoryImportEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var normalizedEntries = entries
            .Select(NormalizeEntry)
            .GroupBy(x => x.TitleId, StringComparer.Ordinal)
            .Select(x => x.First())
            .ToArray();

        if (normalizedEntries.Length == 0)
        {
            return new RentalHistoryImportResult(0, 0, 0, 0, []);
        }

        var ids = normalizedEntries.Select(x => x.TitleId).ToArray();
        var existingDiscs = await dbContext.Discs
            .Include(x => x.ReviewReasons)
            .Where(x => ids.Contains(x.DiscasId))
            .ToDictionaryAsync(x => x.DiscasId, StringComparer.Ordinal, cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var createdCount = 0;
        var markedRentedCount = 0;
        var alreadyRentedCount = 0;
        var priorityDiscIds = new List<long>();
        var createdDiscs = new List<Disc>();

        foreach (var entry in normalizedEntries)
        {
            if (!existingDiscs.TryGetValue(entry.TitleId, out var disc))
            {
                disc = CreateImportedDisc(entry, now);
                dbContext.Discs.Add(disc);
                createdDiscs.Add(disc);
                createdCount++;
                markedRentedCount++;
                continue;
            }

            var wasRented = disc.IsRented;
            if (!wasRented)
            {
                disc.IsRented = true;
                disc.NeedsReview = false;
                disc.LastReviewedAt = now;
                dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
                markedRentedCount++;
            }
            else
            {
                alreadyRentedCount++;
            }

            // 履歴由来であること自体を永続化しておくことで、通常Sourceがなくても
            // 「なぜDBに存在するCDなのか」を後から判別できるようにする。
            disc.RentalHistoryImportedAt ??= now;
            disc.IsArchived = false;

            if (!disc.DetailRefreshCompleted)
            {
                priorityDiscIds.Add(disc.Id);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        // 新規作成DiscはSaveChanges後に内部IDが確定するため、この時点で優先取得対象へ加える。
        priorityDiscIds.AddRange(createdDiscs.Where(x => !x.DetailRefreshCompleted).Select(x => x.Id));

        return new RentalHistoryImportResult(
            normalizedEntries.Length,
            createdCount,
            markedRentedCount,
            alreadyRentedCount,
            priorityDiscIds.Distinct().ToArray());
    }

    private static RentalHistoryImportEntry NormalizeEntry(RentalHistoryImportEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var titleId = entry.TitleId?.Trim() ?? string.Empty;
        var title = entry.Title?.Trim() ?? string.Empty;
        var artist = entry.Artist?.Trim() ?? string.Empty;

        if (titleId.Length is < 1 or > 32 || titleId.Any(x => !char.IsAsciiDigit(x)))
        {
            throw new ArgumentException($"titleIdが不正です: {entry.TitleId}", nameof(entry));
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException($"タイトルが空です: {titleId}", nameof(entry));
        }
        if (string.IsNullOrWhiteSpace(artist))
        {
            throw new ArgumentException($"アーティストが空です: {titleId}", nameof(entry));
        }

        return new RentalHistoryImportEntry(titleId, title, artist);
    }

    private static Disc CreateImportedDisc(RentalHistoryImportEntry entry, DateTime now)
    {
        return new Disc
        {
            DiscasId = entry.TitleId,
            ProductUrl = $"https://www.discas.net/netdvd/cd/goodsDetail.do?titleID={entry.TitleId}",
            Title = entry.Title,
            NormalizedTitle = DiscTextNormalizer.Normalize(entry.Title),
            Artist = entry.Artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(entry.Artist),
            // レンタル履歴HTMLにはジャンル情報がない。空値にすると一覧のジャンル候補が分かりにくいため、
            // 通常クロールで正式なジャンルを取得するまで明示的な仮値を使用する。
            GenreLarge = "未取得",
            IsMaxiSingle = entry.Title.StartsWith("【MAXI】", StringComparison.Ordinal),
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            IsArchived = false,
            NeedsReview = false,
            LastReviewedAt = now,
            IsRented = true,
            RentalHistoryImportedAt = now
        };
    }
}

/// <summary>
/// ブラウザから抽出するレンタル履歴1件分の最小データを保持する
/// </summary>
/// <param name="TitleId">DISCASの商品titleID。先頭ゼロを保持するため文字列として扱う</param>
/// <param name="Title">履歴画面に表示されている商品名</param>
/// <param name="Artist">履歴画面に表示されているアーティスト名</param>
public sealed record RentalHistoryImportEntry(string TitleId, string Title, string Artist);

/// <summary>
/// レンタル履歴インポートの反映件数と詳細優先取得対象を保持する
/// </summary>
public sealed record RentalHistoryImportResult(
    int InputCount,
    int CreatedCount,
    int MarkedRentedCount,
    int AlreadyRentedCount,
    IReadOnlyList<long> PriorityDiscIds);
