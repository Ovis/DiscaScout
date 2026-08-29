using System.Text.Json;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Pages;

/// <summary>
/// 未チェック、Pickup、全件のCD一覧とレビュー操作、検索条件を提供する
/// </summary>
public sealed class DiscsModel(DiscaScoutDbContext dbContext) : PageModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    [BindProperty(SupportsGet = true, Name = "tab")] public string Tab { get; set; } = "unchecked";
    [BindProperty(SupportsGet = true, Name = "title")] public string? TitleSearch { get; set; }
    [BindProperty(SupportsGet = true, Name = "artist")] public string? ArtistSearch { get; set; }
    [BindProperty(SupportsGet = true, Name = "genre")] public string? Genre { get; set; }
    [BindProperty(SupportsGet = true, Name = "excludeMaxi")] public bool ExcludeMaxi { get; set; }
    [BindProperty(SupportsGet = true, Name = "excludeAlbum")] public bool ExcludeAlbum { get; set; }
    [BindProperty(SupportsGet = true, Name = "rental")] public string Rental { get; set; } = "all";
    [BindProperty(SupportsGet = true, Name = "sort")] public string Sort { get; set; } = "updated";
    [BindProperty(SupportsGet = true, Name = "size")] public int PageSize { get; set; } = 50;
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;
    public IReadOnlyList<Disc> Items { get; private set; } = [];
    public IReadOnlyList<GenreGroup> GenreGroups { get; private set; } = [];
    public int UncheckedCount { get; private set; }
    public int PickupCount { get; private set; }
    public int TotalCount { get; private set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? UndoPayload { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        NormalizeInputs();
        UncheckedCount = await dbContext.Discs.CountAsync(IsUnchecked(), cancellationToken);
        PickupCount = await dbContext.Discs.CountAsync(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled), cancellationToken);
        GenreGroups = await LoadGenreGroupsAsync(cancellationToken);
        var query = dbContext.Discs.AsNoTracking().Include(x => x.ReviewReasons).Include(x => x.Sources).Include(x => x.ArtistMatches).ThenInclude(x => x.ArtistSetting).AsQueryable();
        query = ApplySort(ApplyRentalFilter(ApplyFormatFilter(ApplySearch(ApplyTab(query)))));
        TotalCount = await query.CountAsync(cancellationToken);
        PageNumber = Math.Min(PageNumber, TotalPages);
        Items = await query.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToListAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostReviewedAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == id, cancellationToken);
        SetUndoPayload(disc); disc.NeedsReview = false; disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime; dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken); StatusMessage = $"「{disc.Title}」を確認済みにしました"; return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostRentedAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == id, cancellationToken);
        SetUndoPayload(disc); MarkRented(disc, TimeProvider.System.GetUtcNow().UtcDateTime);
        await dbContext.SaveChangesAsync(cancellationToken); StatusMessage = $"「{disc.Title}」を借りた状態にしました"; return RedirectBack(returnUrl);
    }

    public async Task<IActionResult> OnPostReopenAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleAsync(x => x.Id == id, cancellationToken);
        if (!disc.IsRented) { disc.NeedsReview = true; await dbContext.SaveChangesAsync(cancellationToken); }
        return RedirectBack(returnUrl);
    }

    /// <summary>現在ページに表示されている未チェックCDをまとめて確認済みにする</summary>
    public async Task<IActionResult> OnPostReviewPageAsync(List<long> ids, string? returnUrl, CancellationToken cancellationToken)
    {
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons).Where(x => ids.Contains(x.Id) && x.NeedsReview && !x.IsRented).ToListAsync(cancellationToken);
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        foreach (var disc in discs) { disc.NeedsReview = false; disc.LastReviewedAt = now; dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons); }
        await dbContext.SaveChangesAsync(cancellationToken); UndoPayload = null; StatusMessage = $"現在ページの {discs.Count} 件を確認済みにしました"; return RedirectBack(returnUrl);
    }

    /// <summary>一覧で選択された未レンタルCDをまとめて借りた状態へ変更する</summary>
    public async Task<IActionResult> OnPostRentSelectedAsync(List<long> ids, string? returnUrl, CancellationToken cancellationToken)
    {
        var targetIds = ids.Distinct().ToArray();
        var discs = await dbContext.Discs.Include(x => x.ReviewReasons).Where(x => targetIds.Contains(x.Id) && !x.IsRented).ToListAsync(cancellationToken);
        var now = TimeProvider.System.GetUtcNow().UtcDateTime;
        foreach (var disc in discs) MarkRented(disc, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        // 既存Undoは単一CDの状態だけを保持するため、一括操作後に誤ったUndoを提示しないよう破棄する。
        UndoPayload = null;
        StatusMessage = discs.Count == 0 ? "借りた状態へ変更できるCDが選択されていません" : $"選択した {discs.Count} 件を借りた状態にしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>直前の個別レビュー操作またはレンタル操作を元の状態へ戻す</summary>
    public async Task<IActionResult> OnPostUndoAsync(string undoPayload, string? returnUrl, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.Deserialize<ReviewUndoState>(undoPayload); if (state is null) return RedirectBack(returnUrl);
        var disc = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.Id == state.DiscId, cancellationToken);
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons); disc.IsRented = state.IsRented; disc.NeedsReview = state.NeedsReview; disc.LastReviewedAt = state.LastReviewedAt;
        foreach (var reason in state.Reasons) disc.ReviewReasons.Add(new DiscReviewReason { Reason = reason.Reason, CreatedAt = reason.CreatedAt });
        await dbContext.SaveChangesAsync(cancellationToken); UndoPayload = null; StatusMessage = $"「{disc.Title}」の直前の操作を元に戻しました"; return RedirectBack(returnUrl);
    }

    public static string GetPickupArtists(Disc disc) => string.Join(", ", disc.ArtistMatches.Where(x => x.IsCurrentMatch && !x.ArtistSetting.IsArchived && x.ArtistSetting.IsWatchEnabled).Select(x => x.ArtistSetting.Artist).Distinct(StringComparer.Ordinal));
    public static string FormatReviewReason(DiscReviewReasonType reason) => reason switch { DiscReviewReasonType.New => "新規", DiscReviewReasonType.TitleChanged => "タイトル変更", DiscReviewReasonType.ArtistMatched => "Artist Watch一致", DiscReviewReasonType.Reappeared => "再出現", _ => reason.ToString() };
    public string? GetRentalCategoryLabel(DateOnly? rentalStartDate) { if (rentalStartDate is null) return null; var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TimeProvider.System.GetUtcNow(), JapanTimeZone).DateTime); var elapsedDays = today.DayNumber - rentalStartDate.Value.DayNumber; if (elapsedDays < 0) return "近日リリース"; if (elapsedDays <= 90) return "新作"; if (elapsedDays <= 180) return "準新作"; return "旧作"; }
    public string GetTabUrl(string tab) => BuildListUrl(tab, TitleSearch, ArtistSearch, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetArtistUrl(string artist) => BuildListUrl(Tab, TitleSearch, artist, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetGenreUrl(string genre) => BuildListUrl(Tab, TitleSearch, ArtistSearch, genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetPageUrl(int page) => BuildListUrl(Tab, TitleSearch, ArtistSearch, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, page);

    private void MarkRented(Disc disc, DateTime now)
    {
        disc.IsRented = true; disc.NeedsReview = false; disc.LastReviewedAt = now; dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
    }

    private IQueryable<Disc> ApplyTab(IQueryable<Disc> query) => Tab switch { "pickup" => query.Where(x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled)), "all" when HasSearchFilters() => query, "all" => query.Where(x => !x.IsArchived), _ => query.Where(IsUnchecked()) };
    /// <summary>未チェック一覧へ表示する条件を返す</summary>
    private static System.Linq.Expressions.Expression<Func<Disc, bool>> IsUnchecked() => x => x.NeedsReview && !x.IsRented && (!x.IsArchived || x.ArtistCatalogEntries.Any(c => c.IsActive));
    private IQueryable<Disc> ApplySearch(IQueryable<Disc> query) { foreach (var term in SplitTerms(TitleSearch)) { var normalized = DiscTextNormalizer.Normalize(term); query = query.Where(x => x.NormalizedTitle.Contains(normalized)); } foreach (var term in SplitTerms(ArtistSearch)) { var normalized = DiscTextNormalizer.Normalize(term); query = query.Where(x => x.NormalizedArtist.Contains(normalized)); } if (!string.IsNullOrWhiteSpace(Genre)) query = query.Where(x => x.GenreLarge == Genre || x.GenreMiddle == Genre || x.GenreSmall == Genre); return query; }
    private IQueryable<Disc> ApplyFormatFilter(IQueryable<Disc> query) { if (ExcludeMaxi) query = query.Where(x => !x.IsMaxiSingle); if (ExcludeAlbum) query = query.Where(x => x.IsMaxiSingle); return query; }
    private IQueryable<Disc> ApplyRentalFilter(IQueryable<Disc> query) => Rental switch { "rented" => query.Where(x => x.IsRented), "unrented" => query.Where(x => !x.IsRented), _ => query };
    private IQueryable<Disc> ApplySort(IQueryable<Disc> query) => Sort switch { "rental" => query.OrderByDescending(x => x.RentalStartDate.HasValue).ThenByDescending(x => x.RentalStartDate).ThenBy(x => x.Sources.Where(s => s.IsActive).Select(s => (int?)s.SourceRank).Min() ?? int.MaxValue).ThenByDescending(x => x.LastUpdatedAt), "title" => query.OrderBy(x => x.NormalizedTitle).ThenBy(x => x.Id), "artist" => query.OrderBy(x => x.NormalizedArtist).ThenBy(x => x.NormalizedTitle), _ => query.OrderByDescending(x => x.LastUpdatedAt).ThenByDescending(x => x.Id) };

    private async Task<IReadOnlyList<GenreGroup>> LoadGenreGroupsAsync(CancellationToken cancellationToken)
    {
        // ジャンルマスタは持たないため、実際に観測した大・中・小ジャンルの組み合わせから階層を復元する。
        var genres = await dbContext.Discs.AsNoTracking().Select(x => new { x.GenreLarge, x.GenreMiddle, x.GenreSmall }).Distinct().ToListAsync(cancellationToken);
        return genres.GroupBy(x => x.GenreLarge, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(largeGroup => new GenreGroup(largeGroup.Key, largeGroup.Where(x => !string.IsNullOrWhiteSpace(x.GenreMiddle)).GroupBy(x => x.GenreMiddle!, StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).Select(middleGroup => new GenreMiddleGroup(middleGroup.Key, middleGroup.Where(x => !string.IsNullOrWhiteSpace(x.GenreSmall)).Select(x => x.GenreSmall!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray())).ToArray())).ToArray();
    }

    private void SetUndoPayload(Disc disc) => UndoPayload = JsonSerializer.Serialize(new ReviewUndoState(disc.Id, disc.IsRented, disc.NeedsReview, disc.LastReviewedAt, disc.ReviewReasons.Select(x => new ReviewReasonUndoState(x.Reason, x.CreatedAt)).ToArray()));
    private bool HasSearchFilters() => !string.IsNullOrWhiteSpace(TitleSearch) || !string.IsNullOrWhiteSpace(ArtistSearch) || !string.IsNullOrWhiteSpace(Genre) || ExcludeMaxi || ExcludeAlbum;
    private static IEnumerable<string> SplitTerms(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private void NormalizeInputs() { if (Tab is not ("unchecked" or "pickup" or "all")) Tab = "unchecked"; if (Rental is not ("all" or "rented" or "unrented")) Rental = "all"; if (Sort is not ("updated" or "rental" or "title" or "artist")) Sort = "updated"; if (PageSize is not (50 or 100 or 200)) PageSize = 50; PageNumber = Math.Max(1, PageNumber); TitleSearch = string.IsNullOrWhiteSpace(TitleSearch) ? null : TitleSearch.Trim(); ArtistSearch = string.IsNullOrWhiteSpace(ArtistSearch) ? null : ArtistSearch.Trim(); Genre = string.IsNullOrWhiteSpace(Genre) ? null : Genre.Trim(); }
    private IActionResult RedirectBack(string? returnUrl) => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToPage();
    private static string BuildListUrl(string tab, string? title, string? artist, string? genre, bool excludeMaxi, bool excludeAlbum, string rental, string sort, int size, int page) { var parameters = new Dictionary<string, string?> { ["tab"] = tab, ["title"] = title, ["artist"] = artist, ["genre"] = genre, ["excludeMaxi"] = excludeMaxi ? "true" : null, ["excludeAlbum"] = excludeAlbum ? "true" : null, ["rental"] = rental == "all" ? null : rental, ["sort"] = sort == "updated" ? null : sort, ["size"] = size == 50 ? null : size.ToString(), ["p"] = page <= 1 ? null : page.ToString() }; return QueryHelpers.AddQueryString("/discs", parameters.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value)); }
    public sealed record GenreGroup(string Name, IReadOnlyList<GenreMiddleGroup> MiddleGenres);
    public sealed record GenreMiddleGroup(string Name, IReadOnlyList<string> SmallGenres);
    private sealed record ReviewUndoState(long DiscId, bool IsRented, bool NeedsReview, DateTime? LastReviewedAt, IReadOnlyList<ReviewReasonUndoState> Reasons);
    private sealed record ReviewReasonUndoState(DiscReviewReasonType Reason, DateTime CreatedAt);
}
