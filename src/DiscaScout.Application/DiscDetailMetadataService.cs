using System.Net;
using System.Text.RegularExpressions;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DiscaScout.Application;

/// <summary>DISCAS詳細ページの取得要否を判定し、CDの補完メタデータへ反映する</summary>
public sealed partial class DiscDetailMetadataService(
    DiscaScoutDbContext dbContext,
    DiscasPageFetcher pageFetcher,
    DiscasDiscDetailParser parser,
    GenreResolver genreResolver,
    ILogger<DiscDetailMetadataService> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FailedAttemptRetryInterval = TimeSpan.FromHours(6);
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>現在の詳細情報補完の進捗件数と失敗後待機中のCDを取得する</summary>
    public async Task<DiscDetailFetchProgress> GetProgressAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var retryBefore = now - FailedAttemptRetryInterval;
        var today = GetJapanToday(now);
        var incomplete = dbContext.Discs.AsNoTracking().Where(x => !x.DetailRefreshCompleted);
        var total = await incomplete.CountAsync(cancellationToken);
        var dueNow = await incomplete.CountAsync(x =>
            (x.DetailFetchedAt == null && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= retryBefore))
            || (x.DetailFetchedAt != null
                && x.RentalStartDate != null
                && x.RentalStartDate <= today
                && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= x.DetailFetchedAt || x.DetailLastAttemptAt <= retryBefore)), cancellationToken);

        // 件数表示と対象一覧が別条件にならないよう、失敗後クールダウンの条件は同じクエリへ集約する。
        // DetailLastAttemptAtが直前の成功より新しい場合、その試行は成功完了していないため失敗後待機として扱う。
        var retryCooldownQuery = incomplete.Where(x =>
            x.DetailLastAttemptAt != null
            && x.DetailLastAttemptAt > retryBefore
            && (x.DetailFetchedAt == null || x.DetailLastAttemptAt > x.DetailFetchedAt)
            && (x.DetailFetchedAt == null || (x.RentalStartDate != null && x.RentalStartDate <= today)));
        var retryCooldown = await retryCooldownQuery.CountAsync(cancellationToken);
        var retryCooldownRows = await retryCooldownQuery
            .OrderBy(x => x.DetailLastAttemptAt)
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.DiscasId,
                x.Title,
                x.Artist,
                x.DetailFetchedAt,
                x.DetailLastAttemptAt
            })
            .ToListAsync(cancellationToken);
        var retryCooldownItems = retryCooldownRows
            .Select(x => new DiscDetailRetryCooldownItem(
                x.Id,
                x.DiscasId,
                x.Title,
                x.Artist,
                x.DetailFetchedAt,
                x.DetailLastAttemptAt!.Value,
                x.DetailLastAttemptAt.Value + FailedAttemptRetryInterval))
            .ToArray();

        var waitingForRentalStart = await incomplete.CountAsync(x => x.DetailFetchedAt != null && x.RentalStartDate != null && x.RentalStartDate > today, cancellationToken);
        return new DiscDetailFetchProgress(total, dueNow, retryCooldown, waitingForRentalStart, retryCooldownItems);
    }

    /// <summary>現在取得すべき詳細情報があるCDを1件返す</summary>
    public async Task<long?> GetNextDueDiscIdAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var retryBefore = now - FailedAttemptRetryInterval;
        var today = GetJapanToday(now);
        return await dbContext.Discs.AsNoTracking()
            .Where(x => !x.DetailRefreshCompleted)
            .Where(x =>
                (x.DetailFetchedAt == null && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= retryBefore))
                || (x.DetailFetchedAt != null
                    && x.RentalStartDate != null
                    && x.RentalStartDate <= today
                    && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= x.DetailFetchedAt || x.DetailLastAttemptAt <= retryBefore)))
            .OrderBy(x => x.RentalHistoryImportedAt == null)
            .ThenBy(x => x.DetailFetchedAt != null)
            .ThenBy(x => x.DetailLastAttemptAt)
            .ThenBy(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>指定CDが現在詳細取得対象か判定する</summary>
    public async Task<bool> IsDueAsync(long discId, CancellationToken cancellationToken = default)
    {
        var state = await dbContext.Discs.AsNoTracking()
            .Where(x => x.Id == discId)
            .Select(x => new { x.DetailRefreshCompleted, x.DetailFetchedAt, x.DetailLastAttemptAt, x.RentalStartDate })
            .SingleOrDefaultAsync(cancellationToken);
        if (state is null || state.DetailRefreshCompleted) return false;

        var now = clock.GetUtcNow().UtcDateTime;
        var retryBefore = now - FailedAttemptRetryInterval;
        if (state.DetailFetchedAt is null)
            return state.DetailLastAttemptAt is null || state.DetailLastAttemptAt <= retryBefore;

        if (state.RentalStartDate is null || state.RentalStartDate > GetJapanToday(now)) return false;

        // レンタル開始前に成功した取得の直後は開始日到来時に再取得できるようにする一方、
        // その再取得が失敗した場合はDetailLastAttemptAtが直前の成功時刻より新しくなるため6時間待機する。
        return state.DetailLastAttemptAt is null
            || state.DetailLastAttemptAt <= state.DetailFetchedAt
            || state.DetailLastAttemptAt <= retryBefore;
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
            disc.DetailLastAttemptAt = clock.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);

            var result = await pageFetcher.FetchAsync(new Uri(disc.ProductUrl), cancellationToken);
            if (result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
                throw new HttpRequestException($"DISCAS詳細ページの取得に失敗した: {(int)result.StatusCode} {result.StatusCode}");

            var detail = parser.Parse(result.Html, result.FinalUri);
            var fetchedAt = clock.GetUtcNow().UtcDateTime;
            var today = GetJapanToday(fetchedAt);

            // 詳細ページは商品単位の最終情報源として扱う。検索一覧と異なるジャンルへ解決された場合も
            // 詳細側を採用し、差異はログへ残す。
            var resolvedGenre = await genreResolver.ResolveAsync(detail.GenrePath, cancellationToken);
            if (detail.GenrePath.Count > 0 && resolvedGenre is null)
            {
                logger.LogWarning(
                    "詳細ページのジャンルをマスターへ解決できませんでした: DiscId={DiscId}, DiscasId={DiscasId}, GenrePath={GenrePath}",
                    disc.Id,
                    disc.DiscasId,
                    string.Join(" > ", detail.GenrePath));
            }
            else if (resolvedGenre is not null && disc.GenreId is not null && disc.GenreId != resolvedGenre.Id)
            {
                logger.LogWarning(
                    "検索結果と詳細ページのジャンルが異なるため詳細側へ更新します: DiscId={DiscId}, DiscasId={DiscasId}, OldGenreId={OldGenreId}, NewGenreId={NewGenreId}",
                    disc.Id,
                    disc.DiscasId,
                    disc.GenreId,
                    resolvedGenre.Id);
            }

            // 詳細側のジャンルをマスターへ解決できた場合だけ上書きする。
            // HTML変更や一時的なマスター不整合で解決できないときに、検索一覧で正常解決済みのGenreIdまで失わないためである。
            if (resolvedGenre is not null)
            {
                disc.GenreId = resolvedGenre.Id;
            }

            // レンタル履歴だけから作成したDiscは一覧クロールの正式メタデータを持たないため、
            // 詳細ページ取得時にタイトルとアーティストも補完する。
            if (disc.RentalHistoryImportedAt is not null)
            {
                disc.Title = detail.Title;
                disc.NormalizedTitle = DiscTextNormalizer.Normalize(detail.Title);
                disc.Artist = detail.Artist;
                disc.NormalizedArtist = DiscTextNormalizer.Normalize(detail.Artist);
                disc.IsMaxiSingle = detail.Title.StartsWith("【MAXI】", StringComparison.Ordinal);
            }

            disc.RentalStartDate = detail.RentalStartDate;
            disc.Description = detail.Description;
            disc.IsTwoDisc = detail.IsTwoDisc;
            disc.DetailImageUrl = detail.DetailImageUrl;
            if (!string.IsNullOrWhiteSpace(detail.DetailImageUrl)) disc.ImageUrl = ToSmallJacketUrl(detail.DetailImageUrl);
            disc.DetailFetchedAt = fetchedAt;

            // 詳細ページ自体を正常に取得できたにもかかわらずレンタル開始日が存在しない場合は終端状態とする。
            // サイト側の一時的な表示異常が疑われる場合は、詳細画面の手動再取得で明示的にやり直せる。
            disc.DetailRefreshCompleted = !detail.RentalStartDate.HasValue || detail.RentalStartDate.Value <= today;

            dbContext.DiscTracks.RemoveRange(disc.Tracks);
            disc.Tracks.Clear();
            foreach (var track in detail.Tracks)
                disc.Tracks.Add(new DiscTrack { TrackNumber = track.TrackNumber, Title = track.Title, Duration = track.Duration });

            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("DISCAS詳細取得が完了しました: DiscId={DiscId}, DiscasId={DiscasId}, Title={Title}, Artist={Artist}, RentalStartDate={RentalStartDate}, TrackCount={TrackCount}, IsTwoDisc={IsTwoDisc}, RefreshCompleted={RefreshCompleted}", disc.Id, disc.DiscasId, disc.Title, disc.Artist, detail.RentalStartDate, detail.Tracks.Count, detail.IsTwoDisc, disc.DetailRefreshCompleted);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DISCAS詳細取得に失敗しました: DiscId={DiscId}, DiscasId={DiscasId}, Title={Title}, Artist={Artist}, Url={Url}", disc.Id, disc.DiscasId, disc.Title, disc.Artist, disc.ProductUrl);
            throw;
        }
    }

    private static string ToSmallJacketUrl(string detailImageUrl) =>
        MediumJacketSuffixRegex().Replace(detailImageUrl, "${prefix}SX${extension}");

    private static DateOnly GetJapanToday(DateTime utcDateTime)
    {
        var japanTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc), JapanTimeZone);
        return DateOnly.FromDateTime(japanTime);
    }

    [GeneratedRegex(@"(?<prefix>_\d*)MX(?<extension>\.[A-Za-z0-9]+)(?=$|[?#])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MediumJacketSuffixRegex();
}

/// <summary>詳細情報バックグラウンド補完の進捗件数と待機対象を保持する</summary>
public sealed record DiscDetailFetchProgress(
    int IncompleteTotal,
    int DueNow,
    int RetryCooldown,
    int WaitingForRentalStart,
    IReadOnlyList<DiscDetailRetryCooldownItem> RetryCooldownItems)
{
    /// <summary>既知の待機区分に該当しない未完了件数</summary>
    public int OtherIncomplete => Math.Max(0, IncompleteTotal - DueNow - RetryCooldown - WaitingForRentalStart);
}

/// <summary>詳細取得失敗後の6時間クールダウン中にあるCDの運用表示情報を保持する</summary>
public sealed record DiscDetailRetryCooldownItem(
    long Id,
    string DiscasId,
    string Title,
    string Artist,
    DateTime? LastSuccessfulFetchAt,
    DateTime LastAttemptAt,
    DateTime RetryAfter);
