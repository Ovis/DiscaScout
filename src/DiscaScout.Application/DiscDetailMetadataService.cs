using System.Net;
using System.Text.RegularExpressions;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscaScout.Application;

/// <summary>DISCAS詳細ページの取得要否を判定し、CDの補完メタデータへ反映する</summary>
public sealed partial class DiscDetailMetadataService(DiscaScoutDbContext dbContext, DiscasPageFetcher pageFetcher, DiscasDiscDetailParser parser, ILogger<DiscDetailMetadataService> logger, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FailedAttemptRetryInterval = TimeSpan.FromHours(6);
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>現在の詳細情報補完の進捗件数を取得する</summary>
    public async Task<DiscDetailFetchProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime; var retryBefore = now - FailedAttemptRetryInterval; var today = GetJapanToday(now);
        var incomplete = dbContext.Discs.AsNoTracking().Where(x => !x.DetailRefreshCompleted);
        var total = await incomplete.CountAsync(cancellationToken);
        var dueNow = await incomplete.CountAsync(x => (x.DetailFetchedAt == null && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= retryBefore)) || (x.DetailFetchedAt != null && x.RentalStartDate != null && x.RentalStartDate <= today), cancellationToken);
        var retryCooldown = await incomplete.CountAsync(x => x.DetailFetchedAt == null && x.DetailLastAttemptAt != null && x.DetailLastAttemptAt > retryBefore, cancellationToken);
        var waitingForRentalStart = await incomplete.CountAsync(x => x.DetailFetchedAt != null && x.RentalStartDate != null && x.RentalStartDate > today, cancellationToken);
        return new DiscDetailFetchProgress(total, dueNow, retryCooldown, waitingForRentalStart);
    }

    /// <summary>
    /// #38の不正な詳細ジャンル解析で汚染されたレンタル履歴由来CDを未取得状態へ戻し、再取得対象にする
    /// </summary>
    /// <returns>修復対象として初期化したCD件数</returns>
    public async Task<int> RepairCorruptedImportedGenresAsync(CancellationToken cancellationToken = default)
    {
        // 通常のDISCASジャンル名として成立しない長さや、ページナビゲーション・JavaScript断片を含むものだけを対象にする。
        // レンタル履歴インポート由来に限定し、通常クロールで取得した既存ジャンルへ影響させない。
        var corrupted = dbContext.Discs
            .Where(x => x.RentalHistoryImportedAt != null && x.GenreLarge != "未取得")
            .Where(x => x.GenreLarge.Length > 200
                || x.GenreLarge.Contains("すべてのジャンル")
                || x.GenreLarge.Contains("document.")
                || x.GenreLarge.Contains("function(")
                || x.GenreLarge.Contains("javascript"));

        var repaired = await corrupted.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.GenreLarge, "未取得")
            .SetProperty(x => x.GenreMiddle, (string?)null)
            .SetProperty(x => x.GenreSmall, (string?)null)
            .SetProperty(x => x.DetailFetchedAt, (DateTime?)null)
            .SetProperty(x => x.DetailLastAttemptAt, (DateTime?)null)
            .SetProperty(x => x.DetailRefreshCompleted, false), cancellationToken);

        if (repaired > 0)
            logger.LogWarning("不正な詳細ジャンルを検出したため再取得対象へ戻しました: Count={Count}", repaired);

        return repaired;
    }

    /// <summary>現在取得すべき詳細情報があるCDを1件返す</summary>
    public async Task<long?> GetNextDueDiscIdAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime; var retryBefore = now - FailedAttemptRetryInterval; var today = GetJapanToday(now);
        return await dbContext.Discs.AsNoTracking().Where(x => !x.DetailRefreshCompleted)
            .Where(x => (x.DetailFetchedAt == null && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= retryBefore)) || (x.DetailFetchedAt != null && x.RentalStartDate != null && x.RentalStartDate <= today))
            // レンタル履歴インポート直後は過去に借りたCDの情報を早く揃えたいので、通常の未取得CDより先に選ぶ。
            .OrderBy(x => x.RentalHistoryImportedAt == null).ThenBy(x => x.DetailFetchedAt != null).ThenBy(x => x.DetailLastAttemptAt).ThenBy(x => x.Id)
            .Select(x => (long?)x.Id).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>指定CDが現在詳細取得対象か判定する</summary>
    public async Task<bool> IsDueAsync(long discId, CancellationToken cancellationToken = default)
    {
        var state = await dbContext.Discs.AsNoTracking().Where(x => x.Id == discId).Select(x => new { x.DetailRefreshCompleted, x.DetailFetchedAt, x.DetailLastAttemptAt, x.RentalStartDate }).SingleOrDefaultAsync(cancellationToken);
        if (state is null || state.DetailRefreshCompleted) return false;
        var now = clock.GetUtcNow().UtcDateTime;
        if (state.DetailFetchedAt is null) return state.DetailLastAttemptAt is null || state.DetailLastAttemptAt <= now - FailedAttemptRetryInterval;
        return state.RentalStartDate is not null && state.RentalStartDate <= GetJapanToday(now);
    }

    /// <summary>指定CDの詳細ページを取得して補完メタデータを保存する</summary>
    public async Task<bool> FetchAsync(long discId, CancellationToken cancellationToken = default)
    {
        if (!await IsDueAsync(discId, cancellationToken)) return false;
        var disc = await dbContext.Discs.Include(x => x.Tracks).SingleOrDefaultAsync(x => x.Id == discId, cancellationToken);
        if (disc is null) return false;
        logger.LogInformation("DISCAS詳細取得を開始します: DiscId={DiscId}, DiscasId={DiscasId}, Title={Title}, Artist={Artist}, Url={Url}", disc.Id, disc.DiscasId, disc.Title, disc.Artist, disc.ProductUrl);
        try
        {
            disc.DetailLastAttemptAt = clock.GetUtcNow().UtcDateTime; await dbContext.SaveChangesAsync(cancellationToken);
            var result = await pageFetcher.FetchAsync(new Uri(disc.ProductUrl), cancellationToken);
            if (result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices) throw new HttpRequestException($"DISCAS詳細ページの取得に失敗した: {(int)result.StatusCode} {result.StatusCode}");
            var detail = parser.Parse(result.Html, result.FinalUri); var fetchedAt = clock.GetUtcNow().UtcDateTime; var today = GetJapanToday(fetchedAt);

            // 履歴だけから新規作成したDiscのタイトル・アーティスト・ジャンルはインポートJSON由来の仮値であるため、
            // 未取得ジャンルのDiscだけ詳細ページから得た正式値で補完する。
            if (disc.RentalHistoryImportedAt is not null && disc.GenreLarge == "未取得")
            {
                disc.Title = detail.Title; disc.NormalizedTitle = DiscTextNormalizer.Normalize(detail.Title); disc.Artist = detail.Artist; disc.NormalizedArtist = DiscTextNormalizer.Normalize(detail.Artist); disc.IsMaxiSingle = detail.Title.StartsWith("【MAXI】", StringComparison.Ordinal);
                if (!string.IsNullOrWhiteSpace(detail.GenreLarge))
                {
                    disc.GenreLarge = detail.GenreLarge;
                    disc.GenreMiddle = null;
                    disc.GenreSmall = null;
                }
            }

            disc.RentalStartDate = detail.RentalStartDate; disc.Description = detail.Description; disc.IsTwoDisc = detail.IsTwoDisc;
            disc.DetailImageUrl = detail.DetailImageUrl;
            if (!string.IsNullOrWhiteSpace(detail.DetailImageUrl))
            {
                // 詳細画面ではMX画像を直接表示する一方、一覧で大量表示するローカルキャッシュは軽量なSXだけを保持する。
                disc.ImageUrl = ToSmallJacketUrl(detail.DetailImageUrl);
            }
            disc.DetailFetchedAt = fetchedAt; disc.DetailRefreshCompleted = detail.RentalStartDate <= today;
            dbContext.DiscTracks.RemoveRange(disc.Tracks); disc.Tracks.Clear();
            foreach (var track in detail.Tracks) disc.Tracks.Add(new DiscTrack { TrackNumber = track.TrackNumber, Title = track.Title, Duration = track.Duration });
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("DISCAS詳細取得が完了しました: DiscId={DiscId}, DiscasId={DiscasId}, Title={Title}, Artist={Artist}, RentalStartDate={RentalStartDate}, TrackCount={TrackCount}, IsTwoDisc={IsTwoDisc}, RefreshCompleted={RefreshCompleted}", disc.Id, disc.DiscasId, disc.Title, disc.Artist, detail.RentalStartDate, detail.Tracks.Count, detail.IsTwoDisc, disc.DetailRefreshCompleted);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DISCAS詳細取得に失敗しました: DiscId={DiscId}, DiscasId={DiscasId}, Title={Title}, Artist={Artist}, Url={Url}", disc.Id, disc.DiscasId, disc.Title, disc.Artist, disc.ProductUrl);
            throw;
        }
    }

    private static string ToSmallJacketUrl(string detailImageUrl)
    {
        // サイズ識別子はファイル名末尾だけを書き換え、URL途中に偶然含まれるMXには触れない。
        return MediumJacketSuffixRegex().Replace(detailImageUrl, "${prefix}SX${extension}");
    }

    private static DateOnly GetJapanToday(DateTime utcDateTime)
    {
        var japanTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), JapanTimeZone);
        return DateOnly.FromDateTime(japanTime);
    }

    [GeneratedRegex(@"(?<prefix>_\d*)MX(?<extension>\.[A-Za-z0-9]+)(?=$|[?#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediumJacketSuffixRegex();
}

/// <summary>詳細情報バックグラウンド補完の進捗件数を保持する</summary>
public sealed record DiscDetailFetchProgress(int IncompleteTotal, int DueNow, int RetryCooldown, int WaitingForRentalStart)
{
    /// <summary>既知の待機区分に該当しない未完了件数</summary>
    public int OtherIncomplete => Math.Max(0, IncompleteTotal - DueNow - RetryCooldown - WaitingForRentalStart);
}
