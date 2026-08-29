using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Pages;

/// <summary>
/// Artist Watchと全作品収集の設定・手動収集を管理する
/// </summary>
public sealed class ArtistsModel(
    DiscaScoutDbContext dbContext,
    ArtistWatchService artistWatchService,
    ArtistCatalogCollectionService catalogCollectionService,
    ScrapeExecutionGate executionGate) : PageModel
{
    /// <summary>Artist設定一覧</summary>
    public IReadOnlyList<ArtistSettingRow> Settings { get; private set; } = [];

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
    /// Artist設定を追加し、必要なら初回の全作品収集まで実行する
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
            var collected = await executionGate.TryRunAsync(
                ct => catalogCollectionService.CollectAsync(setting.Id, ct),
                cancellationToken);
            StatusMessage = collected is null
                ? "設定を追加しました。別の取得処理が実行中のため全作品収集は開始していません"
                : $"設定を追加し、全作品収集を完了しました（採用 {collected.MatchedCount} 件）";
        }
        else
        {
            StatusMessage = "Artist設定を追加しました";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// Artist設定を更新し、全作品収集が有効なら変更後条件で再収集する
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
        await artistWatchService.UpdateAsync(
            id,
            artist,
            matchType,
            isWatchEnabled,
            collectFullCatalog,
            reopenExistingReviewedMatches,
            cancellationToken);

        if (collectFullCatalog)
        {
            var collected = await executionGate.TryRunAsync(
                ct => catalogCollectionService.CollectAsync(id, ct),
                cancellationToken);
            StatusMessage = collected is null
                ? "設定を保存しました。別の取得処理が実行中のため全作品再収集は開始していません"
                : $"設定を保存し、全作品を再収集しました（採用 {collected.MatchedCount} 件）";
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
    /// 指定Artist設定の全作品を手動で再取得する
    /// </summary>
    public async Task<IActionResult> OnPostCollectAsync(long id, CancellationToken cancellationToken)
    {
        var collected = await executionGate.TryRunAsync(
            ct => catalogCollectionService.CollectAsync(id, ct),
            cancellationToken);
        StatusMessage = collected is null
            ? "別の取得処理が実行中のため全作品収集は開始していません"
            : $"全作品収集を完了しました（検索 {collected.SearchResultCount} 件 / 採用 {collected.MatchedCount} 件）";
        return RedirectToPage();
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
