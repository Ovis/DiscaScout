using System.Text.Json;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Controllers;

/// <summary>
/// CD一覧の検索・表示とレビュー操作を提供する
/// </summary>
[Route("discs")]
public sealed class DiscsController(DiscaScoutDbContext dbContext) : Controller
{
    /// <summary>未チェック、Pickup、全件のCD一覧を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "tab")] string tab = "unchecked",
        [FromQuery(Name = "title")] string? titleSearch = null,
        [FromQuery(Name = "artist")] string? artistSearch = null,
        [FromQuery(Name = "genre")] string? genre = null,
        [FromQuery(Name = "excludeMaxi")] bool excludeMaxi = false,
        [FromQuery(Name = "excludeAlbum")] bool excludeAlbum = false,
        [FromQuery(Name = "rental")] string rental = "all",
        [FromQuery(Name = "sort")] string sort = "updated",
        [FromQuery(Name = "size")] int pageSize = 50,
        [FromQuery(Name = "p")] int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        NormalizeInputs(ref tab, ref titleSearch, ref artistSearch, ref genre, ref rental, ref sort, ref pageSize, ref pageNumber);

        var uncheckedCount = await dbContext.Discs.CountAsync(IsUnchecked(), cancellationToken);
        var pickupCount = await dbContext.Discs.CountAsync(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled), cancellationToken);
        var genreGroups = await LoadGenreGroupsAsync(cancellationToken);

        var query = dbContext.Discs.AsNoTracking()
            .Include(x => x.ReviewReasons)
            .Include(x => x.Sources)
            .Include(x => x.ArtistMatches).ThenInclude(x => x.ArtistSetting)
            .AsQueryable();
        query = ApplyTab(query, tab, HasSearchFilters(titleSearch, artistSearch, genre, excludeMaxi, excludeAlbum));
        query = ApplySearch(query, titleSearch, artistSearch, genre);
        query = ApplyFormatFilter(query, excludeMaxi, excludeAlbum);
        query = ApplyRentalFilter(query, rental);
        query = ApplySort(query, sort);

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        pageNumber = Math.Min(pageNumber, totalPages);
        var items = await query.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        return View(new DiscsViewModel
        {
            Tab = tab,
            TitleSearch = titleSearch,
            ArtistSearch = artistSearch,
            Genre = genre,
            ExcludeMaxi = excludeMaxi,
            ExcludeAlbum = excludeAlbum,
            Rental = rental,
            Sort = sort,
            PageSize = pageSize,
            PageNumber = pageNumber,
            Items = items,
            GenreGroups = genreGroups,
            UncheckedCount = uncheckedCount,
            PickupCount = pickupCount,
            TotalCount = totalCount,
            StatusMessage = TempData[nameof(DiscsViewModel.StatusMessage)] as string,
            UndoPayload = TempData[nameof(DiscsViewModel.UndoPayload)] as string
        });
    }

    /// <summary>指定CDを確認済みにする</summary>
    [HttpPost("reviewed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reviewed(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == id, cancellationToken);
        SetUndoPayload(disc);
        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData[nameof(DiscsViewModel.StatusMessage)] = $"「{disc.Title}」を確認済みにしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>指定CDを借りた状態にする</summary>
    [HttpPost("rented")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rented(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == id, cancellationToken);
        SetUndoPayload(disc);
        MarkRented(disc, TimeProvider.System.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData[nameof(DiscsViewModel.StatusMessage)] = $"「{disc.Title}」を借りた状態にしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>指定CDを手動で未チェックへ戻す</summary>
    [HttpPost("reopen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleAsync(x => x.Id == id, cancellationToken);
        if (!disc.IsRented)
        {
            disc.NeedsReview = true;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return RedirectBack(returnUrl);
    }

    /// <summary>現在ページに表示されている未チェックCDをまとめて確認済みにする</summary>
    [HttpPost("review-page")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewPage(List<long> ids, string? returnUrl, CancellationToken cancellationToken)
    {
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons).Where(x => ids.Contains(x.Id) && x.NeedsReview && !x.IsRented).ToListAsync(cancellationToken);
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        foreach (var disc in discs)
        {
            disc.NeedsReview = false;
            disc.LastReviewedAt = now;
            dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData.Remove(nameof(DiscsViewModel.UndoPayload));
        TempData[nameof(DiscsViewModel.StatusMessage)] = $"現在ページの {discs.Count} 件を確認済みにしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>一覧で選択された未レンタルCDをまとめて借りた状態へ変更する</summary>
    [HttpPost("rent-selected")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RentSelected(List<long> ids, string? returnUrl, CancellationToken cancellationToken)
    {
        var targetIds = ids.Distinct().ToArray();
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons).Where(x => targetIds.Contains(x.Id) && !x.IsRented).ToListAsync(cancellationToken);
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        foreach (var disc in discs) MarkRented(disc, now);
        await dbContext.SaveChangesAsync(cancellationToken);

        // 既存Undoは単一CDの状態だけを保持するため、一括操作後に誤ったUndoを提示しないよう破棄する。
        TempData.Remove(nameof(DiscsViewModel.UndoPayload));
        TempData[nameof(DiscsViewModel.StatusMessage)] = discs.Count == 0
            ? "借りた状態へ変更できるCDが選択されていません"
            : $"選択した {discs.Count} 件を借りた状態にしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>直前の個別レビュー操作またはレンタル操作を元の状態へ戻す</summary>
    [HttpPost("undo")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Undo(string undoPayload, string? returnUrl, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.Deserialize<ReviewUndoState>(undoPayload);
        if (state is null) return RedirectBack(returnUrl);

        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == state.DiscId, cancellationToken);
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        disc.IsRented = state.IsRented;
        disc.NeedsReview = state.NeedsReview;
        disc.LastReviewedAt = state.LastReviewedAt;
        foreach (var reason in state.Reasons)
        {
            disc.ReviewReasons.Add(new DiscReviewReason { Reason = reason.Reason, CreatedAt = reason.CreatedAt });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData.Remove(nameof(DiscsViewModel.UndoPayload));
        TempData[nameof(DiscsViewModel.StatusMessage)] = $"「{disc.Title}」の直前の操作を元に戻しました";
        return RedirectBack(returnUrl);
    }

    private void MarkRented(Disc disc, DateTime now)
    {
        disc.IsRented = true;
        disc.NeedsReview = false;
        disc.LastReviewedAt = now;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
    }

    private void SetUndoPayload(Disc disc) => TempData[nameof(DiscsViewModel.UndoPayload)] = JsonSerializer.Serialize(
        new ReviewUndoState(disc.Id, disc.IsRented, disc.NeedsReview, disc.LastReviewedAt,
            disc.ReviewReasons.Select(x => new ReviewReasonUndoState(x.Reason, x.CreatedAt)).ToArray()));

    private IActionResult RedirectBack(string? returnUrl) => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
        ? LocalRedirect(returnUrl)
        : RedirectToAction(nameof(Index));

    private async Task<IReadOnlyList<DiscsViewModel.GenreGroup>> LoadGenreGroupsAsync(CancellationToken cancellationToken)
    {
        // ジャンルマスタは持たないため、実際に観測した大・中・小ジャンルの組み合わせから階層を復元する。
        var genres = await dbContext.Discs.AsNoTracking().Select(x => new { x.GenreLarge, x.GenreMiddle, x.GenreSmall }).Distinct().ToListAsync(cancellationToken);
        return genres.GroupBy(x => x.GenreLarge, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(largeGroup => new DiscsViewModel.GenreGroup(
                largeGroup.Key,
                largeGroup.Where(x => !string.IsNullOrWhiteSpace(x.GenreMiddle))
                    .GroupBy(x => x.GenreMiddle!, StringComparer.Ordinal)
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(middleGroup => new DiscsViewModel.GenreMiddleGroup(
                        middleGroup.Key,
                        middleGroup.Where(x => !string.IsNullOrWhiteSpace(x.GenreSmall))
                            .Select(x => x.GenreSmall!)
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToArray()))
                    .ToArray()))
            .ToArray();
    }

    private static IQueryable<Disc> ApplyTab(IQueryable<Disc> query, string tab, bool hasSearchFilters) => tab switch
    {
        "pickup" => query.Where(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled)),
        "all" when hasSearchFilters => query,
        "all" => query.Where(x => !x.IsArchived),
        _ => query.Where(IsUnchecked())
    };

    private static System.Linq.Expressions.Expression<Func<Disc, bool>> IsUnchecked() =>
        x => x.NeedsReview && !x.IsRented && (!x.IsArchived || x.ArtistCatalogEntries.Any(c => c.IsActive));

    private static IQueryable<Disc> ApplySearch(IQueryable<Disc> query, string? titleSearch, string? artistSearch, string? genre)
    {
        foreach (var term in SplitTerms(titleSearch))
        {
            var normalized = DiscTextNormalizer.Normalize(term);
            query = query.Where(x => x.NormalizedTitle.Contains(normalized));
        }
        foreach (var term in SplitTerms(artistSearch))
        {
            var normalized = DiscTextNormalizer.Normalize(term);
            query = query.Where(x => x.NormalizedArtist.Contains(normalized));
        }
        if (!string.IsNullOrWhiteSpace(genre)) query = query.Where(x => x.GenreLarge == genre || x.GenreMiddle == genre || x.GenreSmall == genre);
        return query;
    }

    private static IQueryable<Disc> ApplyFormatFilter(IQueryable<Disc> query, bool excludeMaxi, bool excludeAlbum)
    {
        if (excludeMaxi) query = query.Where(x => !x.IsMaxiSingle);
        if (excludeAlbum) query = query.Where(x => x.IsMaxiSingle);
        return query;
    }

    private static IQueryable<Disc> ApplyRentalFilter(IQueryable<Disc> query, string rental) => rental switch
    {
        "rented" => query.Where(x => x.IsRented),
        "unrented" => query.Where(x => !x.IsRented),
        _ => query
    };

    private static IQueryable<Disc> ApplySort(IQueryable<Disc> query, string sort) => sort switch
    {
        "rental" => query.OrderByDescending(x => x.RentalStartDate.HasValue)
            .ThenByDescending(x => x.RentalStartDate)
            .ThenBy(x => x.Sources.Where(s => s.IsActive).Select(s => (int?)s.SourceRank).Min() ?? int.MaxValue)
            .ThenByDescending(x => x.LastUpdatedAt),
        "title" => query.OrderBy(x => x.NormalizedTitle).ThenBy(x => x.Id),
        "artist" => query.OrderBy(x => x.NormalizedArtist).ThenBy(x => x.NormalizedTitle),
        _ => query.OrderByDescending(x => x.LastUpdatedAt).ThenByDescending(x => x.Id)
    };

    private static bool HasSearchFilters(string? titleSearch, string? artistSearch, string? genre, bool excludeMaxi, bool excludeAlbum) =>
        !string.IsNullOrWhiteSpace(titleSearch) || !string.IsNullOrWhiteSpace(artistSearch) || !string.IsNullOrWhiteSpace(genre) || excludeMaxi || excludeAlbum;

    private static IEnumerable<string> SplitTerms(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void NormalizeInputs(ref string tab, ref string? titleSearch, ref string? artistSearch, ref string? genre, ref string rental, ref string sort, ref int pageSize, ref int pageNumber)
    {
        if (tab is not ("unchecked" or "pickup" or "all")) tab = "unchecked";
        if (rental is not ("all" or "rented" or "unrented")) rental = "all";
        if (sort is not ("updated" or "rental" or "title" or "artist")) sort = "updated";
        if (pageSize is not (50 or 100 or 200)) pageSize = 50;
        pageNumber = Math.Max(1, pageNumber);
        titleSearch = string.IsNullOrWhiteSpace(titleSearch) ? null : titleSearch.Trim();
        artistSearch = string.IsNullOrWhiteSpace(artistSearch) ? null : artistSearch.Trim();
        genre = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim();
    }

    private sealed record ReviewUndoState(long DiscId, bool IsRented, bool NeedsReview, DateTime? LastReviewedAt, IReadOnlyList<ReviewReasonUndoState> Reasons);
    private sealed record ReviewReasonUndoState(DiscReviewReasonType Reason, DateTime CreatedAt);
}
