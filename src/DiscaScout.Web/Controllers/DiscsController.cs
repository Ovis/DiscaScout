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
    /// <summary>未チェック、Pickup、レンタル済み、全件のCD一覧を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery(Name = "tab")] string tab = "unchecked",
        [FromQuery(Name = "uncheckedFilter")] string uncheckedFilter = "all",
        [FromQuery(Name = "title")] string? titleSearch = null,
        [FromQuery(Name = "searchDescription")] bool searchDescription = false,
        [FromQuery(Name = "searchTracks")] bool searchTracks = false,
        [FromQuery(Name = "artist")] string? artistSearch = null,
        [FromQuery(Name = "genreLarge")] long? genreLargeId = null,
        [FromQuery(Name = "genreMiddle")] long? genreMiddleId = null,
        [FromQuery(Name = "genreSmall")] long? genreSmallId = null,
        [FromQuery(Name = "excludeMaxi")] bool excludeMaxi = false,
        [FromQuery(Name = "excludeAlbum")] bool excludeAlbum = false,
        [FromQuery(Name = "rental")] string rental = "all",
        [FromQuery(Name = "sort")] string sort = "updated",
        [FromQuery(Name = "size")] int pageSize = 50,
        [FromQuery(Name = "p")] int pageNumber = 1,
        CancellationToken cancellationToken = default)
    {
        NormalizeInputs(ref tab, ref uncheckedFilter, ref titleSearch, ref artistSearch, ref rental, ref sort, ref pageSize, ref pageNumber);
        var genreGroups = await LoadGenreGroupsAsync(cancellationToken);
        NormalizeGenreSelection(genreGroups, ref genreLargeId, ref genreMiddleId, ref genreSmallId);

        var uncheckedCount = await dbContext.Discs.CountAsync(IsUnchecked(), cancellationToken);
        var pickupCount = await dbContext.Discs.CountAsync(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled), cancellationToken);
        var rentedCount = await dbContext.Discs.CountAsync(x => x.IsRented && !x.IsArchived, cancellationToken);

        var query = dbContext.Discs.AsNoTracking()
            .Include(x => x.ReviewReasons)
            .Include(x => x.Sources)
            .Include(x => x.ArtistMatches).ThenInclude(x => x.ArtistSetting)
            .Include(x => x.Genre).ThenInclude(x => x!.Parent).ThenInclude(x => x!.Parent)
            .AsQueryable();

        var selectedGenreId = genreSmallId ?? genreMiddleId ?? genreLargeId;
        var descendantGenreIds = selectedGenreId.HasValue
            ? await LoadDescendantGenreIdsAsync(selectedGenreId.Value, cancellationToken)
            : [];

        query = ApplyTab(query, tab, HasSearchFilters(titleSearch, artistSearch, selectedGenreId, excludeMaxi, excludeAlbum));
        query = ApplyUncheckedFilter(query, tab, uncheckedFilter);
        query = ApplySearch(query, titleSearch, searchDescription, searchTracks, artistSearch, descendantGenreIds);
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
            UncheckedFilter = uncheckedFilter,
            TitleSearch = titleSearch,
            SearchDescription = searchDescription,
            SearchTracks = searchTracks,
            ArtistSearch = artistSearch,
            GenreLargeId = genreLargeId,
            GenreMiddleId = genreMiddleId,
            GenreSmallId = genreSmallId,
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
            RentedCount = rentedCount,
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
            disc.ReviewReasons.Add(new DiscReviewReason { Reason = reason.Reason, CreatedAt = reason.CreatedAt });
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

    private async Task<IReadOnlyList<DiscsViewModel.GenreOption>> LoadGenreGroupsAsync(CancellationToken cancellationToken)
    {
        // 現在有効なジャンルに加え、既存Discが参照しているInactiveジャンルも過去データの検索用に表示する。
        var referencedIds = await dbContext.Discs.AsNoTracking()
            .Where(x => x.GenreId != null)
            .Select(x => x.GenreId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var genres = await dbContext.Genres.AsNoTracking()
            .Where(x => x.IsActive || referencedIds.Contains(x.Id))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var byParent = genres.ToLookup(x => x.ParentId);
        return byParent[null].Select(x => BuildGenreOption(x, byParent, 0)).ToArray();
    }

    private static DiscsViewModel.GenreOption BuildGenreOption(Genre genre, ILookup<long?, Genre> byParent, int depth)
    {
        // 画面はDISCASの現行UIに合わせて3段選択とする。内部Genreモデル自体は任意深度を維持する。
        var children = depth >= 2
            ? []
            : byParent[genre.Id].OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => BuildGenreOption(x, byParent, depth + 1)).ToArray();
        return new DiscsViewModel.GenreOption(genre.Id, genre.Name, genre.IsActive, children);
    }

    private async Task<long[]> LoadDescendantGenreIdsAsync(long selectedGenreId, CancellationToken cancellationToken)
    {
        var genres = await dbContext.Genres.AsNoTracking().Select(x => new { x.Id, x.ParentId }).ToListAsync(cancellationToken);
        var result = new HashSet<long> { selectedGenreId };
        var frontier = new Queue<long>();
        frontier.Enqueue(selectedGenreId);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var child in genres.Where(x => x.ParentId == parent))
            {
                if (result.Add(child.Id)) frontier.Enqueue(child.Id);
            }
        }
        return result.ToArray();
    }

    private static void NormalizeGenreSelection(
        IReadOnlyList<DiscsViewModel.GenreOption> groups,
        ref long? largeId,
        ref long? middleId,
        ref long? smallId)
    {
        // ref引数をLINQ式へ直接取り込めないため、検索前に値をローカルへ退避する。
        var selectedLargeId = largeId;
        var large = selectedLargeId.HasValue ? groups.SingleOrDefault(x => x.Id == selectedLargeId.Value) : null;
        if (large is null)
        {
            largeId = null;
            middleId = null;
            smallId = null;
            return;
        }

        var selectedMiddleId = middleId;
        var middle = selectedMiddleId.HasValue ? large.Children.SingleOrDefault(x => x.Id == selectedMiddleId.Value) : null;
        if (middle is null)
        {
            middleId = null;
            smallId = null;
            return;
        }

        var selectedSmallId = smallId;
        if (selectedSmallId.HasValue && middle.Children.All(x => x.Id != selectedSmallId.Value)) smallId = null;
    }

    private static IQueryable<Disc> ApplyTab(IQueryable<Disc> query, string tab, bool hasSearchFilters) => tab switch
    {
        "pickup" => query.Where(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled)),
        "rented" => query.Where(x => x.IsRented && !x.IsArchived),
        "all" when hasSearchFilters => query,
        "all" => query.Where(x => !x.IsArchived),
        _ => query.Where(IsUnchecked())
    };

    private static System.Linq.Expressions.Expression<Func<Disc, bool>> IsUnchecked() =>
        x => x.NeedsReview && !x.IsRented && (!x.IsArchived || x.ArtistCatalogEntries.Any(c => c.IsActive));

    private static IQueryable<Disc> ApplyUncheckedFilter(IQueryable<Disc> query, string tab, string filter)
    {
        if (tab != "unchecked") return query;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(TimeProvider.System.GetUtcNow(), "Asia/Tokyo").DateTime);
        return filter switch
        {
            "upcoming" => query.Where(x => x.RentalStartDate != null && x.RentalStartDate > today),
            "new" => query.Where(x => x.ReviewReasons.Any(r => r.Reason == DiscReviewReasonType.New)),
            "artist-watch" => query.Where(x => x.ReviewReasons.Any(r => r.Reason == DiscReviewReasonType.ArtistMatched)),
            _ => query
        };
    }

    private static IQueryable<Disc> ApplySearch(
        IQueryable<Disc> query,
        string? titleSearch,
        bool searchDescription,
        bool searchTracks,
        string? artistSearch,
        IReadOnlyCollection<long> genreIds)
    {
        foreach (var term in SplitTerms(titleSearch))
        {
            var rawTerm = term;
            var normalized = DiscTextNormalizer.Normalize(term);
            query = query.Where(x =>
                x.NormalizedTitle.Contains(normalized)
                || (searchDescription && x.Description != null && x.Description.Contains(rawTerm))
                || (searchTracks && x.Tracks.Any(track => track.Title.Contains(rawTerm))));
        }
        foreach (var term in SplitTerms(artistSearch))
        {
            var normalized = DiscTextNormalizer.Normalize(term);
            query = query.Where(x => x.NormalizedArtist.Contains(normalized));
        }
        if (genreIds.Count > 0) query = query.Where(x => x.GenreId != null && genreIds.Contains(x.GenreId.Value));
        return query;
    }

    private static IQueryable<Disc> ApplyFormatFilter(IQueryable<Disc> query, bool excludeMaxi, bool excludeAlbum)
    {
        if (excludeMaxi) query = query.Where(x => !x.IsMaxiSingle);
        if (excludeAlbum) query = query.Where(x => x.IsMaxiSingle);
        return query;
    }

    private static IQueryable<Disc> ApplyRentalFilter(IQueryable<Disc> query, string rental)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(TimeProvider.System.GetUtcNow(), "Asia/Tokyo").DateTime);
        return rental switch
        {
            "upcoming" => query.Where(x => x.RentalStartDate != null && x.RentalStartDate > today),
            "new" => query.Where(x => x.RentalStartDate != null && x.RentalStartDate <= today && x.RentalStartDate >= today.AddDays(-90)),
            "semi-new" => query.Where(x => x.RentalStartDate != null && x.RentalStartDate < today.AddDays(-90) && x.RentalStartDate >= today.AddDays(-180)),
            "old" => query.Where(x => x.RentalStartDate != null && x.RentalStartDate < today.AddDays(-180)),
            _ => query
        };
    }

    private static IQueryable<Disc> ApplySort(IQueryable<Disc> query, string sort) => sort switch
    {
        "rental-asc" => query.OrderBy(x => x.RentalStartDate == null).ThenBy(x => x.RentalStartDate).ThenBy(x => x.Id),
        "rental-desc" => query.OrderBy(x => x.RentalStartDate == null).ThenByDescending(x => x.RentalStartDate).ThenBy(x => x.Id),
        "title" => query.OrderBy(x => x.NormalizedTitle).ThenBy(x => x.Id),
        _ => query.OrderByDescending(x => x.LastUpdatedAt).ThenByDescending(x => x.Id)
    };

    private static bool HasSearchFilters(string? title, string? artist, long? genreId, bool excludeMaxi, bool excludeAlbum) =>
        !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(artist) || genreId.HasValue || excludeMaxi || excludeAlbum;

    private static IEnumerable<string> SplitTerms(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void NormalizeInputs(
        ref string tab,
        ref string uncheckedFilter,
        ref string? titleSearch,
        ref string? artistSearch,
        ref string rental,
        ref string sort,
        ref int pageSize,
        ref int pageNumber)
    {
        tab = new[] { "unchecked", "pickup", "rented", "all" }.Contains(tab) ? tab : "unchecked";
        uncheckedFilter = new[] { "all", "upcoming", "new", "artist-watch" }.Contains(uncheckedFilter) ? uncheckedFilter : "all";
        rental = new[] { "all", "upcoming", "new", "semi-new", "old" }.Contains(rental) ? rental : "all";
        sort = new[] { "updated", "rental-asc", "rental-desc", "title" }.Contains(sort) ? sort : "updated";
        pageSize = new[] { 20, 50, 100, 200 }.Contains(pageSize) ? pageSize : 50;
        pageNumber = Math.Max(1, pageNumber);
        titleSearch = string.IsNullOrWhiteSpace(titleSearch) ? null : titleSearch.Trim();
        artistSearch = string.IsNullOrWhiteSpace(artistSearch) ? null : artistSearch.Trim();
    }

    private sealed record ReviewUndoState(long DiscId, bool IsRented, bool NeedsReview, DateTime? LastReviewedAt, ReviewReasonUndoState[] Reasons);
    private sealed record ReviewReasonUndoState(DiscReviewReasonType Reason, DateTime CreatedAt);
}
