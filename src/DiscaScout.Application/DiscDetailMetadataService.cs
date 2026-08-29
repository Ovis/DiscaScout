using System.Net;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Application;

/// <summary>
/// DISCAS詳細ページの取得要否を判定し、CDの補完メタデータへ反映する
/// </summary>
public sealed class DiscDetailMetadataService(
    DiscaScoutDbContext dbContext,
    DiscasPageFetcher pageFetcher,
    DiscasDiscDetailParser parser,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan FailedAttemptRetryInterval = TimeSpan.FromHours(6);
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 現在取得すべき詳細情報があるCDを1件返す
    /// </summary>
    /// <remarks>
    /// 未取得CDを優先し、レンタル開始前に一度取得したCDは開始日を迎えるまで対象外にする。
    /// 取得失敗は6時間空け、恒久的なHTML変更等で同一ページへ短時間に繰り返しアクセスしない。
    /// </remarks>
    public async Task<long?> GetNextDueDiscIdAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var retryBefore = now - FailedAttemptRetryInterval;
        var today = GetJapanToday(now);

        return await dbContext.Discs
            .AsNoTracking()
            .Where(x => !x.DetailRefreshCompleted)
            .Where(x =>
                (x.DetailFetchedAt == null
                    && (x.DetailLastAttemptAt == null || x.DetailLastAttemptAt <= retryBefore))
                || (x.DetailFetchedAt != null
                    && x.RentalStartDate != null
                    && x.RentalStartDate <= today))
            .OrderBy(x => x.DetailFetchedAt != null)
            .ThenBy(x => x.DetailLastAttemptAt)
            .ThenBy(x => x.Id)
            .Select(x => (long?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// 指定CDが現在詳細取得対象か判定する
    /// </summary>
    /// <param name="discId">Discの内部ID</param>
    public async Task<bool> IsDueAsync(long discId, CancellationToken cancellationToken = default)
    {
        var state = await dbContext.Discs
            .AsNoTracking()
            .Where(x => x.Id == discId)
            .Select(x => new
            {
                x.DetailRefreshCompleted,
                x.DetailFetchedAt,
                x.DetailLastAttemptAt,
                x.RentalStartDate
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (state is null || state.DetailRefreshCompleted)
        {
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (state.DetailFetchedAt is null)
        {
            return state.DetailLastAttemptAt is null
                || state.DetailLastAttemptAt <= now - FailedAttemptRetryInterval;
        }

        return state.RentalStartDate is not null
            && state.RentalStartDate <= GetJapanToday(now);
    }

    /// <summary>
    /// 指定CDの詳細ページを取得して補完メタデータを保存する
    /// </summary>
    /// <param name="discId">Discの内部ID</param>
    /// <param name="cancellationToken">HTTP取得とDB更新を中断するためのトークン</param>
    /// <returns>実際に取得を行った場合true。対象外またはCDが存在しない場合false</returns>
    public async Task<bool> FetchAsync(long discId, CancellationToken cancellationToken = default)
    {
        if (!await IsDueAsync(discId, cancellationToken))
        {
            return false;
        }

        var disc = await dbContext.Discs
            .Include(x => x.Tracks)
            .SingleOrDefaultAsync(x => x.Id == discId, cancellationToken);
        if (disc is null)
        {
            return false;
        }

        var attemptAt = clock.GetUtcNow().UtcDateTime;
        disc.DetailLastAttemptAt = attemptAt;

        // 失敗時刻も永続化しておくことで、HTML変更や一時障害が起きたときに
        // BackgroundServiceが同じ商品を短時間で何度も再試行しないようにする。
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = await pageFetcher.FetchAsync(new Uri(disc.ProductUrl), cancellationToken);
        if (result.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
        {
            throw new HttpRequestException(
                $"DISCAS詳細ページの取得に失敗した: {(int)result.StatusCode} {result.StatusCode}");
        }

        var detail = parser.Parse(result.Html, result.FinalUri);
        var fetchedAt = clock.GetUtcNow().UtcDateTime;
        var today = GetJapanToday(fetchedAt);

        disc.RentalStartDate = detail.RentalStartDate;
        disc.Description = detail.Description;
        disc.IsTwoDisc = detail.IsTwoDisc;
        disc.DetailFetchedAt = fetchedAt;

        // 初回取得時点ですでにレンタル開始日ならその取得で完了する。
        // まだ開始前なら開始日を迎えた後にもう1回だけ取得し、その時点で完了する。
        disc.DetailRefreshCompleted = detail.RentalStartDate <= today;

        dbContext.DiscTracks.RemoveRange(disc.Tracks);
        disc.Tracks.Clear();
        foreach (var track in detail.Tracks)
        {
            disc.Tracks.Add(new DiscTrack
            {
                TrackNumber = track.TrackNumber,
                Title = track.Title,
                Duration = track.Duration
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static DateOnly GetJapanToday(DateTime utcDateTime)
    {
        var japanTime = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc),
            JapanTimeZone);
        return DateOnly.FromDateTime(japanTime);
    }
}
