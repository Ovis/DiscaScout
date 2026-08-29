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
    /// Artist設定を追加し、必要なら初回の全作品収集をBackgroundServiceへ登録する
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

        var current = await dbContext.ArtistSettings
            .AsNoTracking()
            .SingleAsync(x => x.Id == id, cancellationToken);
        var normalizedArtist = DiscTextNormalizer.Normalize(artist);
        var shouldCollect = collectFullCatalog
            && (!current.CollectFullCatalog
                || current.MatchType != matchType
                || !string.Equals(current.NormalizedArtist, normalizedArtist, StringComparison.Ordinal));

        await artistWatchService.UpdateAsync(
            id,
            artist,
            matchType,
            isWatchEnabled,
            collectFullCatalog,
            reopenExistingReviewedMatches,
            cancellationToken);

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

    /// <summary>
    /// Artist設定を論理アーカイブする
    /// </summary>
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

    /// <summary>
    /// アーカイブ済みArtist設定を復元する
    /// </summary>
    public async Task<IActionResult> OnPostRestoreAsync(long id, CancellationToken cancellationToken)
    {
        await artistWatchService.SetArchivedAsync(id, false, false, cancellationToken);
        StatusMessage = "Artist設定を復元しました";
        return RedirectToPage();
    }

    /// <summary>
    /// 指定Artist設定の全作品再取得をBackgroundServiceへ登録する
    /// </summary>
    public async Task<IActionResult> OnPostCollectAsync(long id, CancellationToken cancellationToken)
    {
        var enqueued = await EnqueueCatalogAsync(id, cancellationToken);
        StatusMessage = enqueued
            ? "全作品収集を受け付けました。バックグラウンドで実行します"
            : "このArtistの全作品収集は既に保留中または実行中です";
        return RedirectToPage();
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
        Settings = await dbContext.ArtistSettings
            .AsNoTracking()
            .OrderBy(x => x.IsArchived)
            .ThenBy(x => x.Artist)
            .Select(x => new ArtistSettingRow(
                x.Id,
                x.Artist,
                x.MatchType,
                x.IsWatchEnabled,
                x.CollectFullCatalog,
                x.IsArchived,
                x.DiscMatches.Count(m => m.IsCurrentMatch),
                x.CatalogEntries.Count(c => c.IsActive)))
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
        bool IsArchived,
        int CurrentMatchCount,
        int ActiveCatalogCount);
}
