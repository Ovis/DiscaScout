using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Pages;

/// <summary>
/// Artist Watchと全作品収集の設定・手動収集要求を管理する
/// </summary>
public sealed class ArtistsModel(
    DiscaScoutDbContext dbContext,
    ArtistWatchService artistWatchService,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : PageModel
{
    public IReadOnlyList<ArtistSettingRow> Settings { get; private set; } = [];
    public HashSet<long> ActiveCatalogSettingIds { get; private set; } = [];
    public ArtistSettingPreview? Preview { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    /// <summary>
    /// 設定保存前にローカルCDへの一致影響を確認する
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(
        long? id,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reviewInitialCatalogItems,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            StatusMessage = "アーティスト名を入力してください";
            return RedirectToPage();
        }
        if (id.HasValue && await IsCatalogWorkActiveAsync(id.Value, cancellationToken))
        {
            StatusMessage = "全作品収集が保留中または実行中のため、このArtist設定は変更できません";
            return RedirectToPage();
        }

        await LoadAsync(cancellationToken);
        Preview = await BuildPreviewAsync(id, artist, matchType, isWatchEnabled, collectFullCatalog, reviewInitialCatalogItems, cancellationToken);
        return Page();
    }

    /// <summary>
    /// 新しいArtist設定を作成し、必要なら初回の全作品収集を登録する
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reviewInitialCatalogItems,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            StatusMessage = "アーティスト名を入力してください";
            return RedirectToPage();
        }

        var setting = await artistWatchService.CreateAsync(
            artist, matchType, isWatchEnabled, collectFullCatalog, reopenExistingReviewedMatches, cancellationToken);
        setting.ReviewInitialCatalogItems = collectFullCatalog && reviewInitialCatalogItems;
        await dbContext.SaveChangesAsync(cancellationToken);

        if (collectFullCatalog)
        {
            await EnqueueCatalogAsync(setting.Id, cancellationToken);
            StatusMessage = "設定を追加し、全作品収集を受け付けました";
        }
        else
        {
            StatusMessage = "Artist設定を追加しました";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// Artist設定を更新し、検索条件に影響する変更時は全作品再収集を登録する
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAsync(
        long id,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reviewInitialCatalogItems,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken)
    {
        if (await IsCatalogWorkActiveAsync(id, cancellationToken))
        {
            StatusMessage = "全作品収集が保留中または実行中のため、このArtist設定は変更できません";
            return RedirectToPage();
        }

        var current = await dbContext.ArtistSettings.AsNoTracking().SingleAsync(x => x.Id == id, cancellationToken);
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var shouldCollect = collectFullCatalog
            && (!current.CollectFullCatalog
                || current.MatchType != matchType
                || !string.Equals(current.NormalizedArtist, normalizedArtist, StringComparison.Ordinal));

        await artistWatchService.UpdateAsync(
            id, artist, matchType, isWatchEnabled, collectFullCatalog, reopenExistingReviewedMatches, cancellationToken);

        var setting = await dbContext.ArtistSettings.SingleAsync(x => x.Id == id, cancellationToken);
        if (!setting.InitialCatalogCollectionCompleted)
        {
            setting.ReviewInitialCatalogItems = collectFullCatalog && reviewInitialCatalogItems;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (shouldCollect)
        {
            await EnqueueCatalogAsync(id, cancellationToken);
            StatusMessage = "設定を保存し、全作品再収集を受け付けました";
        }
        else
        {
            StatusMessage = "Artist設定を保存しました";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostArchiveAsync(long id, CancellationToken cancellationToken)
    {
        if (await IsCatalogWorkActiveAsync(id, cancellationToken))
        {
            StatusMessage = "全作品収集が保留中または実行中のため、このArtist設定はアーカイブできません";
            return RedirectToPage();
        }
        await artistWatchService.SetArchivedAsync(id, true, false, cancellationToken);
        StatusMessage = "Artist設定をアーカイブしました";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreAsync(long id, CancellationToken cancellationToken)
    {
        await artistWatchService.SetArchivedAsync(id, false, false, cancellationToken);
        StatusMessage = "Artist設定を復元しました";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCollectAsync(long id, CancellationToken cancellationToken)
    {
        var enqueued = await EnqueueCatalogAsync(id, cancellationToken);
        StatusMessage = enqueued
            ? "全作品収集を受け付けました。バックグラウンドで実行します"
            : "このArtistの全作品収集は既に保留中または実行中です";
        return RedirectToPage();
    }

    private async Task<ArtistSettingPreview> BuildPreviewAsync(
        long? id,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reviewInitialCatalogItems,
        CancellationToken cancellationToken)
    {
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var matchingDiscs = await dbContext.Discs.AsNoTracking()
            .Where(x => matchType == ArtistMatchType.Exact
                ? x.NormalizedArtist == normalizedArtist
                : x.NormalizedArtist.Contains(normalizedArtist))
            .Select(x => new { x.Id, x.NeedsReview, x.IsRented })
            .ToListAsync(cancellationToken);

        HashSet<long> currentMatchIds = [];
        if (id.HasValue)
        {
            currentMatchIds = (await dbContext.DiscArtistMatches.AsNoTracking()
                .Where(x => x.ArtistSettingId == id.Value && x.IsCurrentMatch)
                .Select(x => x.DiscId)
                .ToListAsync(cancellationToken))
                .ToHashSet();

            var settingState = await dbContext.ArtistSettings.AsNoTracking()
                .Where(x => x.Id == id.Value)
                .Select(x => new { x.InitialCatalogCollectionCompleted, x.ReviewInitialCatalogItems })
                .SingleAsync(cancellationToken);

            // 初回取得済みならUIから変更できない値なので、disabled checkboxの未送信値ではなく保存済み設定を引き継ぐ。
            if (settingState.InitialCatalogCollectionCompleted)
            {
                reviewInitialCatalogItems = settingState.ReviewInitialCatalogItems;
            }
        }

        // 再確認の対象になるのは、設定変更によって新たに一致状態へ入る未レンタルCDだけである。
        // 既にこの設定へ一致中のCDはArtistWatchServiceでも再オープンしないため、プレビュー件数も同じ意味に揃える。
        var newlyMatched = matchingDiscs.Where(x => !currentMatchIds.Contains(x.Id)).ToArray();
        var reviewedCount = matchingDiscs.Count(x => !x.NeedsReview);
        var reopenCandidateCount = isWatchEnabled ? newlyMatched.Count(x => !x.NeedsReview && !x.IsRented) : 0;

        return new ArtistSettingPreview(
            id, artist.Trim(), matchType, isWatchEnabled, collectFullCatalog, reviewInitialCatalogItems,
            matchingDiscs.Count, reviewedCount, newlyMatched.Length, reopenCandidateCount);
    }

    private async Task<bool> EnqueueCatalogAsync(long id, CancellationToken cancellationToken)
    {
        var enqueued = await manualWorkStore.TryEnqueueArtistCatalogAsync(id, DateTime.UtcNow, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
        }
        return enqueued;
    }

    private async Task<bool> IsCatalogWorkActiveAsync(long id, CancellationToken cancellationToken)
    {
        var active = await manualWorkStore.GetActiveAsync(cancellationToken);
        return active.Any(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId == id);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Settings = await dbContext.ArtistSettings.AsNoTracking()
            .OrderBy(x => x.IsArchived)
            .ThenBy(x => x.Artist)
            .Select(x => new ArtistSettingRow(
                x.Id, x.Artist, x.MatchType, x.IsWatchEnabled, x.CollectFullCatalog,
                x.ReviewInitialCatalogItems, x.InitialCatalogCollectionCompleted, x.IsArchived,
                x.DiscMatches.Count(m => m.IsCurrentMatch), x.CatalogEntries.Count(c => c.IsActive)))
            .ToListAsync(cancellationToken);

        ActiveCatalogSettingIds = (await manualWorkStore.GetActiveAsync(cancellationToken))
            .Where(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId.HasValue)
            .Select(x => x.ArtistSettingId!.Value)
            .ToHashSet();
    }

    /// <summary>
    /// Artist設定一覧で表示する設定値と現在件数
    /// </summary>
    public sealed record ArtistSettingRow(
        long Id,
        string Artist,
        ArtistMatchType MatchType,
        bool IsWatchEnabled,
        bool CollectFullCatalog,
        bool ReviewInitialCatalogItems,
        bool InitialCatalogCollectionCompleted,
        bool IsArchived,
        int CurrentMatchCount,
        int ActiveCatalogCount);

    /// <summary>
    /// Artist設定の保存前確認で表示する設定値とローカルCDへの影響件数
    /// </summary>
    public sealed record ArtistSettingPreview(
        long? Id,
        string Artist,
        ArtistMatchType MatchType,
        bool IsWatchEnabled,
        bool CollectFullCatalog,
        bool ReviewInitialCatalogItems,
        int MatchCount,
        int ReviewedMatchCount,
        int NewlyMatchedCount,
        int ReopenCandidateCount);
}
