using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Controllers;

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
        var shouldCollect = collectFullCatalog
            && (!current.CollectFullCatalog
                || current.MatchType != matchType
                || !string.Equals(current.NormalizedArtist, normalizedArtist, StringComparison.Ordinal));

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
        return new ArtistsViewModel.ArtistSettingPreview(
            id,
            artist.Trim(),
            matchType,
            isWatchEnabled,
            collectFullCatalog,
            reviewInitialCatalogItems,
            matchingDiscs.Count,
            reviewedCount,
            newlyMatched.Length,
            reopenCandidateCount);
    }

    private async Task<ArtistsViewModel> LoadAsync(ArtistsViewModel.ArtistSettingPreview? preview, CancellationToken cancellationToken)
    {
        var settings = await dbContext.ArtistSettings.AsNoTracking()
            .OrderBy(x => x.IsArchived)
            .ThenBy(x => x.Artist)
            .Select(x => new ArtistsViewModel.ArtistSettingRow(
                x.Id,
                x.Artist,
                x.MatchType,
                x.IsWatchEnabled,
                x.CollectFullCatalog,
                x.ReviewInitialCatalogItems,
                x.InitialCatalogCollectionCompleted,
                x.IsArchived,
                x.DiscMatches.Count(m => m.IsCurrentMatch),
                x.CatalogEntries.Count(c => c.IsActive)))
            .ToListAsync(cancellationToken);

        var activeIds = (await manualWorkStore.GetActiveAsync(cancellationToken))
            .Where(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId.HasValue)
            .Select(x => x.ArtistSettingId!.Value)
            .ToHashSet();

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
