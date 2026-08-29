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
        var pickupCount = await dbContext.Discs.CountAsync(
            x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled),
            cancellationToken);
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
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons)
            .Where(x => ids.Contains(x.Id) && x.NeedsReview && !x.IsRented)
            .ToListAsync(cancellationToken);
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
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons)
            .Where(x => targetIds.Contains(x.Id) && !x.IsRented)
            .ToListAsync(cancellationToken);
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

    private IActionResult RedirectBack(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction(nameof(Index));

    private async Task<IReadOnlyList<DiscsViewModel.GenreGroup>> LoadGenreGroupsAsync(CancellationToken cancellationToken)
    {
        // ジャンルマスタは持たないため、実際に観測した大・中・小ジャンルの組み合わせから階層を復元する。
        var genres = await dbContext.Discs.AsNoTracking()
            .Select(x => new { x.GenreLarge, x.GenreMiddle, x.GenreSmall })
            .Distinct()
            .ToListAsync(cancellationToken);
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
        if (!string.IsNullOrWhiteSpace(genre))
        {
            query = query.Where(x => x.GenreLarge == genre || x.GenreMiddle == genre || x.GenreSmall == genre);
        }
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

/// <summary>
/// CD単体の詳細表示とレビュー・レンタル操作を提供する
/// </summary>
[Route("discs")]
public sealed class DiscDetailController(DiscaScoutDbContext dbContext, DiscDetailFetchSignal detailFetchSignal) : Controller
{
    /// <summary>指定CDの詳細情報を表示する</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detail(long id, [FromQuery] string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await LoadDiscAsync(id, cancellationToken);
        if (disc is null) return NotFound();

        // 保存値は原文のまま維持し、詳細画面の表示モデルだけ句点ごとに改行して可読性を補う。
        if (!string.IsNullOrWhiteSpace(disc.Description))
        {
            disc.Description = disc.Description.Replace("。", $"。{Environment.NewLine}", StringComparison.Ordinal);
        }

        if (!disc.DetailRefreshCompleted)
        {
            // Web要求自体ではDISCASへアクセスせず、優先キューへ通知するだけにして画面表示を待たせない。
            detailFetchSignal.Request(id);
        }

        return View(new DiscDetailViewModel
        {
            Disc = disc,
            ReturnUrl = returnUrl,
            StatusMessage = TempData[nameof(DiscDetailViewModel.StatusMessage)] as string
        });
    }

    /// <summary>CDを確認済みにし、現在のレビュー理由を解消する</summary>
    [HttpPost("{id:long}/reviewed")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reviewed(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null) return NotFound();
        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData[nameof(DiscDetailViewModel.StatusMessage)] = "確認済みにしました";
        return RedirectToDetail(id, returnUrl);
    }

    /// <summary>確認済みCDを手動で未チェックへ戻す</summary>
    [HttpPost("{id:long}/reopen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null) return NotFound();
        if (!disc.IsRented)
        {
            // 手動での再確認要求は自動差分理由ではないため、ReviewReasonを捏造せずNeedsReviewだけを戻す。
            disc.NeedsReview = true;
            await dbContext.SaveChangesAsync(cancellationToken);
            TempData[nameof(DiscDetailViewModel.StatusMessage)] = "未チェックへ戻しました";
        }
        return RedirectToDetail(id, returnUrl);
    }

    /// <summary>CDを借りた状態にし、現在のレビュー理由を解消する</summary>
    [HttpPost("{id:long}/rented")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rented(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null) return NotFound();
        disc.IsRented = true;
        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData[nameof(DiscDetailViewModel.StatusMessage)] = "借りた状態にしました";
        return RedirectToDetail(id, returnUrl);
    }

    /// <summary>CDを未レンタル状態へ戻す</summary>
    [HttpPost("{id:long}/unrented")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unrented(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null) return NotFound();
        disc.IsRented = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        TempData[nameof(DiscDetailViewModel.StatusMessage)] = "未レンタル状態へ戻しました";
        return RedirectToDetail(id, returnUrl);
    }

    private async Task<Disc?> LoadDiscAsync(long id, CancellationToken cancellationToken) => await dbContext.Discs
        .AsNoTracking().AsSplitQuery()
        .Include(x => x.Sources)
        .Include(x => x.ReviewReasons)
        .Include(x => x.ChangeHistory)
        .Include(x => x.Tracks)
        .Include(x => x.ArtistMatches).ThenInclude(x => x.ArtistSetting)
        .Include(x => x.ArtistCatalogEntries).ThenInclude(x => x.ArtistSetting)
        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    private IActionResult RedirectToDetail(long id, string? returnUrl) => RedirectToAction(nameof(Detail), new { id, returnUrl });
}

/// <summary>
/// Artist Watchと全作品収集の設定・手動収集要求を管理する
/// </summary>
[Route("artists")]
public sealed class ArtistsController(
    DiscaScoutDbContext dbContext,
    ArtistWatchService artistWatchService,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : Controller
{
    /// <summary>Artist設定一覧を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await LoadAsync(null, cancellationToken));

    /// <summary>設定保存前にローカルCDへの一致影響を確認する</summary>
    [HttpPost("preview")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Preview(long? id, string artist, ArtistMatchType matchType, bool isWatchEnabled, bool collectFullCatalog, bool reviewInitialCatalogItems, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "アーティスト名を入力してください";
            return RedirectToAction(nameof(Index));
        }
        if (id.HasValue && await IsCatalogWorkActiveAsync(id.Value, cancellationToken))
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "全作品収集が保留中または実行中のため、このArtist設定は変更できません";
            return RedirectToAction(nameof(Index));
        }

        var preview = await BuildPreviewAsync(id, artist, matchType, isWatchEnabled, collectFullCatalog, reviewInitialCatalogItems, cancellationToken);
        return View("Index", await LoadAsync(preview, cancellationToken));
    }

    /// <summary>新しいArtist設定を作成し、必要なら初回の全作品収集を登録する</summary>
    [HttpPost("add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(string artist, ArtistMatchType matchType, bool isWatchEnabled, bool collectFullCatalog, bool reviewInitialCatalogItems, bool reopenExistingReviewedMatches, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "アーティスト名を入力してください";
            return RedirectToAction(nameof(Index));
        }

        var setting = await artistWatchService.CreateAsync(artist, matchType, isWatchEnabled, collectFullCatalog, reopenExistingReviewedMatches, cancellationToken);
        setting.ReviewInitialCatalogItems = collectFullCatalog && reviewInitialCatalogItems;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (collectFullCatalog)
        {
            await EnqueueCatalogAsync(setting.Id, cancellationToken);
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "設定を追加し、全作品収集を受け付けました";
        }
        else
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "Artist設定を追加しました";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Artist設定を更新し、検索条件に影響する変更時は全作品再収集を登録する</summary>
    [HttpPost("update")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(long id, string artist, ArtistMatchType matchType, bool isWatchEnabled, bool collectFullCatalog, bool reviewInitialCatalogItems, bool reopenExistingReviewedMatches, CancellationToken cancellationToken)
    {
        if (await IsCatalogWorkActiveAsync(id, cancellationToken))
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "全作品収集が保留中または実行中のため、このArtist設定は変更できません";
            return RedirectToAction(nameof(Index));
        }

        var current = await dbContext.ArtistSettings.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var shouldCollect = collectFullCatalog && (!current.CollectFullCatalog || current.MatchType != matchType || !string.Equals(current.NormalizedArtist, normalizedArtist, StringComparison.Ordinal));

        await artistWatchService.UpdateAsync(id, artist, matchType, isWatchEnabled, collectFullCatalog, reopenExistingReviewedMatches, cancellationToken);
        var setting = await dbContext.ArtistSettings.SingleAsync(x => x.Id == id, cancellationToken);
        if (!setting.InitialCatalogCollectionCompleted)
        {
            setting.ReviewInitialCatalogItems = collectFullCatalog && reviewInitialCatalogItems;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (shouldCollect)
        {
            await EnqueueCatalogAsync(id, cancellationToken);
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "設定を保存し、全作品再収集を受け付けました";
        }
        else
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "Artist設定を保存しました";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Artist設定をアーカイブする</summary>
    [HttpPost("archive")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(long id, CancellationToken cancellationToken)
    {
        if (await IsCatalogWorkActiveAsync(id, cancellationToken))
        {
            TempData[nameof(ArtistsViewModel.StatusMessage)] = "全作品収集が保留中または実行中のため、このArtist設定はアーカイブできません";
            return RedirectToAction(nameof(Index));
        }
        await artistWatchService.SetArchivedAsync(id, true, false, cancellationToken);
        TempData[nameof(ArtistsViewModel.StatusMessage)] = "Artist設定をアーカイブしました";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>アーカイブ済みArtist設定を復元する</summary>
    [HttpPost("restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(long id, CancellationToken cancellationToken)
    {
        await artistWatchService.SetArchivedAsync(id, false, false, cancellationToken);
        TempData[nameof(ArtistsViewModel.StatusMessage)] = "Artist設定を復元しました";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>指定Artistの全作品再取得をキューへ登録する</summary>
    [HttpPost("collect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Collect(long id, CancellationToken cancellationToken)
    {
        var enqueued = await EnqueueCatalogAsync(id, cancellationToken);
        TempData[nameof(ArtistsViewModel.StatusMessage)] = enqueued
            ? "全作品収集を受け付けました。バックグラウンドで実行します"
            : "このArtistの全作品収集は既に保留中または実行中です";
        return RedirectToAction(nameof(Index));
    }

    private async Task<ArtistsViewModel.ArtistSettingPreview> BuildPreviewAsync(long? id, string artist, ArtistMatchType matchType, bool isWatchEnabled, bool collectFullCatalog, bool reviewInitialCatalogItems, CancellationToken cancellationToken)
    {
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var matchingDiscs = await dbContext.Discs.AsNoTracking()
            .Where(x => matchType == ArtistMatchType.Exact ? x.NormalizedArtist == normalizedArtist : x.NormalizedArtist.Contains(normalizedArtist))
            .Select(x => new { x.Id, x.NeedsReview, x.IsRented })
            .ToListAsync(cancellationToken);

        HashSet<long> currentMatchIds = [];
        if (id.HasValue)
        {
            currentMatchIds = (await dbContext.DiscArtistMatches.AsNoTracking()
                .Where(x => x.ArtistSettingId == id.Value && x.IsCurrentMatch)
                .Select(x => x.DiscId)
                .ToListAsync(cancellationToken)).ToHashSet();
            var settingState = await dbContext.ArtistSettings.AsNoTracking()
                .Where(x => x.Id == id.Value)
                .Select(x => new { x.InitialCatalogCollectionCompleted, x.ReviewInitialCatalogItems })
                .SingleAsync(cancellationToken);
            // 初回取得済みならdisabled checkboxの未送信値ではなく保存済み設定を引き継ぐ。
            if (settingState.InitialCatalogCollectionCompleted) reviewInitialCatalogItems = settingState.ReviewInitialCatalogItems;
        }

        var newlyMatched = matchingDiscs.Where(x => !currentMatchIds.Contains(x.Id)).ToArray();
        var reviewedCount = matchingDiscs.Count(x => !x.NeedsReview);
        var reopenCandidateCount = isWatchEnabled ? newlyMatched.Count(x => !x.NeedsReview && !x.IsRented) : 0;
        return new ArtistsViewModel.ArtistSettingPreview(id, artist.Trim(), matchType, isWatchEnabled, collectFullCatalog, reviewInitialCatalogItems,
            matchingDiscs.Count, reviewedCount, newlyMatched.Length, reopenCandidateCount);
    }

    private async Task<ArtistsViewModel> LoadAsync(ArtistsViewModel.ArtistSettingPreview? preview, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ArtistSettings.AsNoTracking()
            .OrderBy(x => x.IsArchived).ThenBy(x => x.Artist)
            .Select(x => new ArtistsViewModel.ArtistSettingRow(x.Id, x.Artist, x.MatchType, x.IsWatchEnabled, x.CollectFullCatalog,
                x.ReviewInitialCatalogItems, x.InitialCatalogCollectionCompleted, x.IsArchived,
                x.DiscMatches.Count(m => m.IsCurrentMatch), x.CatalogEntries.Count(c => c.IsActive)))
            .ToListAsync(cancellationToken);
        var activeIds = (await manualWorkStore.GetActiveAsync(cancellationToken))
            .Where(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId.HasValue)
            .Select(x => x.ArtistSettingId!.Value).ToHashSet();
        return new ArtistsViewModel
        {
            Settings = settings,
            ActiveCatalogSettingIds = activeIds,
            Preview = preview,
            StatusMessage = TempData[nameof(ArtistsViewModel.StatusMessage)] as string
        };
    }

    private async Task<bool> EnqueueCatalogAsync(long id, CancellationToken cancellationToken)
    {
        var enqueued = await manualWorkStore.TryEnqueueArtistCatalogAsync(id, DateTime.UtcNow, cancellationToken);
        if (enqueued) manualWorkSignal.Notify();
        return enqueued;
    }

    private async Task<bool> IsCatalogWorkActiveAsync(long id, CancellationToken cancellationToken)
    {
        var active = await manualWorkStore.GetActiveAsync(cancellationToken);
        return active.Any(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId == id);
    }
}

/// <summary>
/// 定期取得設定、手動実行要求、実行履歴を管理する運用画面を提供する
/// </summary>
[Route("operations")]
public sealed class OperationsController(
    IScrapeScheduleStore scheduleStore,
    IScrapeOperationsQueryStore operationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : Controller
{
    /// <summary>現在の設定と運用状態を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await LoadAsync(null, cancellationToken));

    /// <summary>定期実行設定を保存する</summary>
    [HttpPost("schedule")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(bool isEnabled, DayOfWeek dayOfWeek, TimeOnly localTime, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(dayOfWeek)) ModelState.AddModelError(nameof(dayOfWeek), "曜日が不正です");
        if (!ModelState.IsValid)
        {
            return View("Index", await LoadOperationalStateAsync(isEnabled, dayOfWeek, localTime, null, cancellationToken));
        }

        await scheduleStore.UpdateAsync(isEnabled, dayOfWeek, localTime, cancellationToken);
        TempData[nameof(OperationsViewModel.StatusMessage)] = isEnabled
            ? $"定期取得を {OperationsViewModel.GetDayLabel(dayOfWeek)} {localTime:HH:mm} に設定しました"
            : "定期取得を無効にしました";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>UpcomingとNewの手動取得をBackgroundServiceへ登録する</summary>
    [HttpPost("run-now")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(CancellationToken cancellationToken)
    {
        var enqueued = await manualWorkStore.TryEnqueueFullScrapeAsync(DateTime.UtcNow, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
            TempData[nameof(OperationsViewModel.StatusMessage)] = "手動取得を受け付けました。バックグラウンドで実行します";
        }
        else
        {
            TempData[nameof(OperationsViewModel.StatusMessage)] = "通常取得系の手動処理は既に保留中または実行中です";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<OperationsViewModel> LoadAsync(string? statusMessage, CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        return await LoadOperationalStateAsync(settings.IsEnabled, settings.DayOfWeek, settings.LocalTime,
            statusMessage ?? TempData[nameof(OperationsViewModel.StatusMessage)] as string, cancellationToken);
    }

    private async Task<OperationsViewModel> LoadOperationalStateAsync(bool isEnabled, DayOfWeek dayOfWeek, TimeOnly localTime, string? statusMessage, CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        return new OperationsViewModel
        {
            IsEnabled = isEnabled,
            DayOfWeek = dayOfWeek,
            LocalTime = localTime,
            LastScheduledExecutionDate = settings.LastScheduledExecutionDate,
            RecentRuns = await operationsQueryStore.GetRecentRunsAsync(30, cancellationToken),
            PendingRetries = await operationsQueryStore.GetPendingRetriesAsync(cancellationToken),
            ActiveManualWork = await manualWorkStore.GetActiveAsync(cancellationToken),
            RecentManualWork = await manualWorkStore.GetRecentAsync(20, cancellationToken),
            StatusMessage = statusMessage
        };
    }
}

/// <summary>
/// Discord通知とスクレイピング件数安全装置の設定画面を提供する
/// </summary>
[Route("settings")]
public sealed class SettingsController(
    DiscordNotificationSettingsStore discordSettingsStore,
    DiscordNotificationService discordNotificationService,
    IScrapeGuardStore scrapeGuardStore,
    IScrapeOperationsStore scrapeOperationsStore,
    IScrapeOperationsQueryStore scrapeOperationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : Controller
{
    private static readonly ScrapeCategory[] GuardCategories = [ScrapeCategory.Upcoming, ScrapeCategory.New];

    /// <summary>保存済み設定を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await LoadAsync(null, null, null, cancellationToken));

    /// <summary>Discord通知設定をSQLiteへ保存する</summary>
    [HttpPost("discord")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDiscord(DiscordNotificationMode discordMode, string? discordWebhookUrl, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(discordMode)) ModelState.AddModelError(nameof(discordMode), "通知モードが不正です");
        if (!string.IsNullOrWhiteSpace(discordWebhookUrl)
            && (!Uri.TryCreate(discordWebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            ModelState.AddModelError(nameof(discordWebhookUrl), "Webhook URLにはHTTPSのURLを指定してください");
        }

        if (!ModelState.IsValid)
        {
            return View("Index", await LoadAsync(discordMode, discordWebhookUrl, null, cancellationToken));
        }
        await discordSettingsStore.UpdateAsync(discordMode, discordWebhookUrl, cancellationToken);
        TempData[nameof(SettingsViewModel.StatusMessage)] = "Discord通知設定を保存しました";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>保存済みWebhookへテスト通知を送信する</summary>
    [HttpPost("discord/test")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestDiscord(CancellationToken cancellationToken)
    {
        try
        {
            await discordNotificationService.SendTestAsync(cancellationToken);
            TempData[nameof(SettingsViewModel.StatusMessage)] = "Discordへテスト通知を送信しました";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // テスト通知ではWebhook設定ミスを利用者へ返す必要があるため、通常通知と異なり失敗を画面へ表示する。
            TempData[nameof(SettingsViewModel.StatusMessage)] = $"Discordへのテスト通知に失敗しました: {ex.Message}";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>急減許可を有効化する前に対象カテゴリと直近異常値を確認表示する</summary>
    [HttpPost("scrape-guard/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareCountDropOverride(ScrapeCategory category, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();
        var model = await LoadAsync(null, null, category, cancellationToken);
        return View("Index", model);
    }

    /// <summary>指定カテゴリの次回1回だけ急減を許可し、そのカテゴリの手動取得をキューへ登録する</summary>
    [HttpPost("scrape-guard/enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableCountDropOverride(ScrapeCategory category, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();
        var now = DateTime.UtcNow;
        await scrapeGuardStore.EnableCountDropOverrideAsync(category, now, cancellationToken);
        var enqueued = await manualWorkStore.TryEnqueueCategoryScrapeAsync(category, now, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
            TempData[nameof(SettingsViewModel.StatusMessage)] = $"{SettingsViewModel.GetCategoryLabel(category)}の急減を次回1回だけ許可し、確認取得を登録しました";
        }
        else
        {
            // 既に同カテゴリまたはFullScrapeが待機・実行中なら、その取得がOverrideを利用できるため重複アクセスは追加しない。
            TempData[nameof(SettingsViewModel.StatusMessage)] = $"{SettingsViewModel.GetCategoryLabel(category)}の急減を次回1回だけ許可しました。既存の通常取得があるため追加の確認取得は登録していません";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>指定カテゴリの未消費の急減許可を取り消す</summary>
    [HttpPost("scrape-guard/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelCountDropOverride(ScrapeCategory category, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();
        await scrapeGuardStore.CancelCountDropOverrideAsync(category, cancellationToken);
        TempData[nameof(SettingsViewModel.StatusMessage)] = $"{SettingsViewModel.GetCategoryLabel(category)}の急減許可を取り消しました";
        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsViewModel> LoadAsync(DiscordNotificationMode? postedMode, string? postedWebhookUrl, ScrapeCategory? confirmationCategory, CancellationToken cancellationToken)
    {
        var settings = await discordSettingsStore.GetAsync(cancellationToken);
        var guards = await LoadScrapeGuardsAsync(cancellationToken);
        return new SettingsViewModel
        {
            DiscordMode = postedMode ?? settings.Mode,
            DiscordWebhookUrl = postedMode.HasValue ? postedWebhookUrl : settings.WebhookUrl,
            StatusMessage = TempData[nameof(SettingsViewModel.StatusMessage)] as string,
            ScrapeGuards = guards,
            CountDropConfirmation = confirmationCategory.HasValue ? guards.Single(x => x.Category == confirmationCategory.Value) : null
        };
    }

    private async Task<IReadOnlyList<SettingsViewModel.ScrapeGuardStatus>> LoadScrapeGuardsAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<SettingsViewModel.ScrapeGuardStatus>(GuardCategories.Length);
        foreach (var category in GuardCategories)
        {
            var guard = await scrapeGuardStore.GetAsync(category, cancellationToken);
            var baseline = await scrapeOperationsStore.GetLastAcceptedRunAsync(category, cancellationToken);
            var anomaly = await scrapeOperationsQueryStore.GetLatestAbnormalCountRunAsync(category, cancellationToken);
            statuses.Add(new SettingsViewModel.ScrapeGuardStatus(category, guard, baseline, anomaly));
        }
        return statuses;
    }
}
