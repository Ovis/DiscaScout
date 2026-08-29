using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Pages;

/// <summary>
/// 未チェック、Pickup、全件のCD一覧とレビュー状態変更を提供する
/// </summary>
public sealed class DiscsModel(DiscaScoutDbContext dbContext) : PageModel
{
    /// <summary>表示対象タブ</summary>
    [BindProperty(SupportsGet = true, Name = "tab")]
    public string Tab { get; set; } = "unchecked";

    /// <summary>タイトル・アーティスト横断検索語</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    /// <summary>レンタル状態フィルター</summary>
    [BindProperty(SupportsGet = true, Name = "rental")]
    public string Rental { get; set; } = "all";

    /// <summary>並び順</summary>
    [BindProperty(SupportsGet = true, Name = "sort")]
    public string Sort { get; set; } = "updated";

    /// <summary>1ページの表示件数</summary>
    [BindProperty(SupportsGet = true, Name = "size")]
    public int PageSize { get; set; } = 50;

    /// <summary>1から始まるページ番号</summary>
    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    /// <summary>現在ページのCD</summary>
    public IReadOnlyList<Disc> Items { get; private set; } = [];

    /// <summary>未チェック件数</summary>
    public int UncheckedCount { get; private set; }

    /// <summary>現在Watchに一致するPickup件数</summary>
    public int PickupCount { get; private set; }

    /// <summary>絞り込み後の総件数</summary>
    public int TotalCount { get; private set; }

    /// <summary>総ページ数</summary>
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));

    /// <summary>処理結果メッセージ</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// 現在条件のCD一覧を読み込む
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        NormalizeInputs();

        UncheckedCount = await dbContext.Discs.CountAsync(
            x => x.NeedsReview && !x.IsRented && !x.IsArchived,
            cancellationToken);
        PickupCount = await dbContext.Discs.CountAsync(
            x => x.ArtistMatches.Any(m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled),
            cancellationToken);

        var query = dbContext.Discs
            .AsNoTracking()
            .Include(x => x.ReviewReasons)
            .Include(x => x.Sources)
            .Include(x => x.ArtistMatches)
                .ThenInclude(x => x.ArtistSetting)
            .AsQueryable();

        query = ApplyTab(query);
        query = ApplySearch(query);
        query = ApplyRentalFilter(query);
        query = ApplySort(query);

        TotalCount = await query.CountAsync(cancellationToken);
        if (PageNumber > TotalPages)
        {
            PageNumber = TotalPages;
        }

        Items = await query
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// CDを確認済みにして現在のレビュー理由を消去する
    /// </summary>
    public async Task<IActionResult> OnPostReviewedAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs
            .Include(x => x.ReviewReasons)
            .SingleAsync(x => x.Id == id, cancellationToken);

        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = $"「{disc.Title}」を確認済みにしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>
    /// CDを借りた状態にし、Inboxから除外する
    /// </summary>
    public async Task<IActionResult> OnPostRentedAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs
            .Include(x => x.ReviewReasons)
            .SingleAsync(x => x.Id == id, cancellationToken);

        disc.IsRented = true;
        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = $"「{disc.Title}」を借りた状態にしました";
        return RedirectBack(returnUrl);
    }

    /// <summary>
    /// 確認済みCDを手動で未チェックへ戻す
    /// </summary>
    public async Task<IActionResult> OnPostReopenAsync(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleAsync(x => x.Id == id, cancellationToken);
        if (!disc.IsRented)
        {
            // 手動での再確認要求は自動差分理由とは別なのでReviewReasonは追加しない。
            // NeedsReviewだけを戻すことで、次の自動変化理由を履歴と混同しないようにする。
            disc.NeedsReview = true;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return RedirectBack(returnUrl);
    }

    /// <summary>
    /// 現在Watchに一致している有効な設定名を表示用に返す
    /// </summary>
    public static string GetPickupArtists(Disc disc) => string.Join(", ", disc.ArtistMatches
        .Where(x => x.IsCurrentMatch && !x.ArtistSetting.IsArchived && x.ArtistSetting.IsWatchEnabled)
        .Select(x => x.ArtistSetting.Artist)
        .Distinct(StringComparer.Ordinal));

    private IQueryable<Disc> ApplyTab(IQueryable<Disc> query)
    {
        return Tab switch
        {
            "pickup" => query.Where(x => x.ArtistMatches.Any(
                m => m.IsCurrentMatch && !m.ArtistSetting.IsArchived && m.ArtistSetting.IsWatchEnabled)),
            "all" when !string.IsNullOrWhiteSpace(Search) => query,
            "all" => query.Where(x => !x.IsArchived),
            _ => query.Where(x => x.NeedsReview && !x.IsRented && !x.IsArchived)
        };
    }

    private IQueryable<Disc> ApplySearch(IQueryable<Disc> query)
    {
        if (string.IsNullOrWhiteSpace(Search))
        {
            return query;
        }

        // 空白区切りの各語についてTitleまたはArtistのどちらかに含まれることを要求し、
        // フィールドをまたいだAND検索を実現する。
        foreach (var term in Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = DiscTextNormalizer.Normalize(term);
            query = query.Where(x => x.NormalizedTitle.Contains(normalized) || x.NormalizedArtist.Contains(normalized));
        }

        return query;
    }

    private IQueryable<Disc> ApplyRentalFilter(IQueryable<Disc> query)
    {
        return Rental switch
        {
            "rented" => query.Where(x => x.IsRented),
            "unrented" => query.Where(x => !x.IsRented),
            _ => query
        };
    }

    private IQueryable<Disc> ApplySort(IQueryable<Disc> query)
    {
        return Sort switch
        {
            "rental" => query.OrderByDescending(x => x.RentalStartDate).ThenByDescending(x => x.LastUpdatedAt),
            "title" => query.OrderBy(x => x.NormalizedTitle).ThenBy(x => x.Id),
            "artist" => query.OrderBy(x => x.NormalizedArtist).ThenBy(x => x.NormalizedTitle),
            _ => query.OrderByDescending(x => x.LastUpdatedAt).ThenByDescending(x => x.Id)
        };
    }

    private void NormalizeInputs()
    {
        if (Tab is not ("unchecked" or "pickup" or "all"))
        {
            Tab = "unchecked";
        }

        if (Rental is not ("all" or "rented" or "unrented"))
        {
            Rental = "all";
        }

        if (Sort is not ("updated" or "rental" or "title" or "artist"))
        {
            Sort = "updated";
        }

        if (PageSize is not (50 or 100 or 200))
        {
            PageSize = 50;
        }

        PageNumber = Math.Max(1, PageNumber);
    }

    private IActionResult RedirectBack(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage();
    }
}
