using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Controllers;

/// <summary>
/// CD単体の詳細表示とレビュー・レンタル・詳細再取得操作を提供する
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
            disc.Description = disc.Description.Replace("。", $"。{Environment.NewLine}", StringComparison.Ordinal);

        if (!disc.DetailRefreshCompleted)
            detailFetchSignal.Request(id);

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

    /// <summary>保存済みの詳細取得状態を未完了へ戻し、バックグラウンドでの再取得を優先要求する</summary>
    [HttpPost("{id:long}/refetch-detail")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefetchDetail(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null) return NotFound();

        disc.DetailRefreshCompleted = false;
        disc.DetailFetchedAt = null;
        disc.DetailLastAttemptAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);

        detailFetchSignal.Request(id);
        TempData[nameof(DiscDetailViewModel.StatusMessage)] = "詳細情報の再取得を要求しました。バックグラウンドで取得します";
        return RedirectToDetail(id, returnUrl);
    }

    private async Task<Disc?> LoadDiscAsync(long id, CancellationToken cancellationToken) => await dbContext.Discs
        .AsNoTracking().AsSplitQuery()
        .Include(x => x.Sources)
        .Include(x => x.ReviewReasons)
        .Include(x => x.ChangeHistory)
        .Include(x => x.Tracks)
        .Include(x => x.Genre).ThenInclude(x => x!.Parent).ThenInclude(x => x!.Parent)
        .Include(x => x.ArtistMatches).ThenInclude(x => x.ArtistSetting)
        .Include(x => x.ArtistCatalogEntries).ThenInclude(x => x.ArtistSetting)
        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    private IActionResult RedirectToDetail(long id, string? returnUrl) => RedirectToAction(nameof(Detail), new { id, returnUrl });
}
