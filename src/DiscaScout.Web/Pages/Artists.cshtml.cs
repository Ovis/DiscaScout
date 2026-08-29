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
    /// <summary>Artist設定一覧</summary>
    public IReadOnlyList<ArtistSettingRow> Settings { get; private set; } = [];

    /// <summary>全作品収集が保留または実行中のArtistSetting ID</summary>
    public HashSet<long> ActiveCatalogSettingIds { get; private set; } = [];

    /// <summary>保存前に確認する一致件数と設定値</summary>
    public ArtistSettingPreview? Preview { get; private set; }

    /// <summary>処理結果メッセージ</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Artist設定と一致件数を表示する
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// 設定を保存する前に、現在のローカルCDへ適用した場合の影響を確認する
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync(
        long? id,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
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
        Preview = await BuildPreviewAsync(id, artist, matchType, isWatchEnabled, collectFullCatalog, cancellationToken);
        return Page();
    }

    /// <summary>
    /// 新しいアーティスト設定を作成し、必要なら初回の全作品収集をBackgroundServiceへ登録する
    /// </summary>
    public async Task<IActionResult> OnPostAddAsync(
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
        bool reopenExistingReviewedMatches,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            StatusMessage = "アーティスト名を入力してください";
            return RedirectToPage();
        }

        var setting = await artistWatchService.CreateAsync(
            artist,
            matchType,
            isWatchEnabled,
            collectFullCatalog,
            reopenExistingReviewedMatches,
            cancellationToken);

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
    /// Artist設定を更新し、検索条件に影響する変更があった場合だけ全作品再収集を登録する
    /// </summary>
    public async Task<IActionResult> OnPostUpdateAsync(
        long id,
        string artist,
        ArtistMatchType matchType,
        bool isWatchEnabled,
        bool collectFullCatalog,
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
            && (!current.CollectFullCatalog || current.MatchType != matchType || !string.Equals(current.NormalizedArtist, normalizedArtist, StringComparison.Ordinal));

        await artistWatchService.UpdateAsync(id, artist, matchType, isWatchEnabled, collectFullCatalog, reopenExistingReviewedMatches, cancellationToken);

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

    /// <summary>Artist設定を論理アーカイブする</summary>
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

    /// <summary>アーカイブ済みArtist設定を復元する</summary>
    public async Task<IActionResult> OnPostRestoreAsync(long id, CancellationToken cancellationToken)
    {
        await artistWatchService.SetArchivedAsync(id, false, false, cancellationToken);
        StatusMessage = "Artist設定を復元しました";
        return RedirectToPage();
    }

    /// <summary>指定Artist設定の全作品再取得をBackgroundServiceへ登録する</summary>
    public async Task<IActionResult> OnPostCollectAsync(long id, CancellationToken cancellationToken)
    {
        var enqueued = await EnqueueCatalogAsync(id, cancellationToken);
        StatusMessage = enqueued ? "全作品収集を受け付けました。バックグラウンドで実行します" : "このArtistの全作品収集は既に保留中または実行中です";
        return RedirectToPage();
    }

    private async Task<ArtistSettingPreview> BuildPreviewAsync(long? id, string artist, ArtistMatchType matchType, bool isWatchEnabled, bool collectFullCatalog, CancellationToken cancellationToken)
    {
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var matchingDiscs = await dbContext.Discs
            .AsNoTracking()
            .Where(x => matchType == ArtistMatchType.Exact
                ? x.NormalizedArtist == normalizedArtist
                : x.NormalizedArtist.Contains(normalizedArtist))
            .Select(x => new { x.Id, x.NeedsReview, x.IsRented })
            .ToListAsync(cancellationToken);

        HashSet<long> currentMatchIds = [];
        if (id.HasValue)
        {
            currentMatchIds = (await dbContext.DiscArtistMatches
                .AsNoTracking()
                .Where(x => x.ArtistSettingId == id.Value && x.IsCurrentMatch)
                .Select(x => x.DiscId)
                .ToListAsync(cancellationToken))
                .ToHashSet();
        }

        // 再確認の対象になるのは、設定変更によって新たに一致状態へ入る未レンタルCDだけである。
        // 既にこの設定へ一致中のCDはArtistWatchServiceでも再オープンしないため、プレビュー件数も同じ意味に揃える。
        var newlyMatched = matchingDiscs.Where(x => !currentMatchIds.Contains(x.Id)).ToArray();
        var reviewedCount = matchingDiscs.Count(x => !x.NeedsReview);
        var reopenCandidateCount = isWatchEnabled ? newlyMatched.Count(x => !x.NeedsReview && !x.IsRented) : 0;

        return new ArtistSettingPreview(
            id,
            artist.Trim(),
            matchType,
            isWatchEnabled,
            collectFullCatalog,
            matchingDiscs.Count,
            reviewedCount,
            newlyMatched.Length,
            reopenCandidateCount);
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Settings = await dbContext.ArtistSettings
            .AsNoTracking()
            .OrderBy(x => x.IsArchived)
            .ThenBy(x => x.Artist)
            .Select(x => new ArtistSettingRow(
                x.Id, x.Artist, x.MatchType, x.IsWatchEnabled, x.CollectFullCatalog, x.IsArchived,
                x.DiscMatches.Count(m => m.IsCurrentMatch), x.CatalogEntries.Count(c => c.IsActive)))
            .ToListAsync(cancellationToken);

        ActiveCatalogSettingIds = (await manualWorkStore.GetActiveAsync(cancellationToken))
            .Where(x => x.Type == ManualWorkType.ArtistCatalog && x.ArtistSettingId.HasValue)
            .Select(x => x.ArtistSettingId!.Value)
            .ToHashSet();
    }

    /// <summary>Artist設定一覧で表示する設定値と現在件数</summary>
    public sealed record ArtistSettingRow(long Id, string Artist, ArtistMatchType MatchType, bool IsWatchEnabled, bool CollectFullCatalog, bool IsArchived, int CurrentMatchCount, int ActiveCatalogCount);

    /// <summary>Artist設定の保存前確認で表示する設定値とローカルCDへの影響件数</summary>
    public sealed record ArtistSettingPreview(long? Id, string Artist, ArtistMatchType MatchType, bool IsWatchEnabled, bool CollectFullCatalog, int MatchCount, int ReviewedMatchCount, int NewlyMatchedCount, int ReopenCandidateCount);
}
