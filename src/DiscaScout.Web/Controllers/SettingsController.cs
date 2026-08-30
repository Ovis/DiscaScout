using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace DiscaScout.Web.Controllers;

/// <summary>
/// Discord通知、スクレイピング件数安全装置、ジャンルマスターの設定画面を提供する
/// </summary>
[Route("settings")]
public sealed class SettingsController(
    DiscordNotificationSettingsStore discordSettingsStore,
    DiscordNotificationService discordNotificationService,
    IScrapeGuardStore scrapeGuardStore,
    IScrapeOperationsStore scrapeOperationsStore,
    IScrapeOperationsQueryStore scrapeOperationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal,
    GenreMasterService genreMasterService) : Controller
{
    private static readonly ScrapeCategory[] GuardCategories = [ScrapeCategory.Upcoming, ScrapeCategory.New];

    /// <summary>保存済み設定を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) =>
        View(await LoadAsync(null, null, null, cancellationToken));

    /// <summary>DISCASのすべてのジャンルページからジャンルマスターを手動更新する</summary>
    [HttpPost("genre-master/refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshGenreMaster(CancellationToken cancellationToken)
    {
        try
        {
            var result = await genreMasterService.RefreshAsync(cancellationToken);
            TempData[nameof(SettingsViewModel.StatusMessage)] =
                $"ジャンルマスターを更新しました。追加 {result.Added}件 / 更新 {result.Updated}件 / 無効化 {result.Deactivated}件 / 再有効化 {result.Reactivated}件";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 外部HTMLの構造変更や件数急減を安全装置で拒否した場合も、既存マスターは維持したまま理由を画面へ返す。
            TempData[nameof(SettingsViewModel.StatusMessage)] = $"ジャンルマスターの更新に失敗しました: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>Discord通知設定をSQLiteへ保存する</summary>
    [HttpPost("discord")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDiscord(
        DiscordNotificationMode discordMode,
        string? discordWebhookUrl,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(discordMode))
        {
            ModelState.AddModelError(nameof(discordMode), "通知モードが不正です");
        }

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
            TempData[nameof(SettingsViewModel.StatusMessage)] = $"Discordへのテスト通知に失敗しました: {ex.Message}";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>急減許可を有効化する前に対象カテゴリと直近異常値を確認表示する</summary>
    [HttpPost("scrape-guard/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareCountDropOverride(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();
        var model = await LoadAsync(null, null, category, cancellationToken);
        return View("Index", model);
    }

    /// <summary>指定カテゴリの次回1回だけ急減を許可し、そのカテゴリの手動取得をキューへ登録する</summary>
    [HttpPost("scrape-guard/enable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableCountDropOverride(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();

        var now = DateTime.UtcNow;
        await scrapeGuardStore.EnableCountDropOverrideAsync(category, now, cancellationToken);
        var enqueued = await manualWorkStore.TryEnqueueCategoryScrapeAsync(category, now, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
            TempData[nameof(SettingsViewModel.StatusMessage)] =
                $"{SettingsViewModel.GetCategoryLabel(category)}の急減を次回1回だけ許可し、確認取得を登録しました";
        }
        else
        {
            TempData[nameof(SettingsViewModel.StatusMessage)] =
                $"{SettingsViewModel.GetCategoryLabel(category)}の急減を次回1回だけ許可しました。既存の通常取得があるため追加の確認取得は登録していません";
        }

        return RedirectToAction(nameof(Index));
    }

    /// <summary>指定カテゴリの未消費の急減許可を取り消す</summary>
    [HttpPost("scrape-guard/cancel")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelCountDropOverride(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();

        await scrapeGuardStore.CancelCountDropOverrideAsync(category, cancellationToken);
        TempData[nameof(SettingsViewModel.StatusMessage)] =
            $"{SettingsViewModel.GetCategoryLabel(category)}の急減許可を取り消しました";
        return RedirectToAction(nameof(Index));
    }

    private async Task<SettingsViewModel> LoadAsync(
        DiscordNotificationMode? postedMode,
        string? postedWebhookUrl,
        ScrapeCategory? confirmationCategory,
        CancellationToken cancellationToken)
    {
        var settings = await discordSettingsStore.GetAsync(cancellationToken);
        var guards = await LoadScrapeGuardsAsync(cancellationToken);
        var genreMaster = await genreMasterService.GetStatusAsync(cancellationToken);
        return new SettingsViewModel
        {
            DiscordMode = postedMode ?? settings.Mode,
            DiscordWebhookUrl = postedMode.HasValue ? postedWebhookUrl : settings.WebhookUrl,
            StatusMessage = TempData[nameof(SettingsViewModel.StatusMessage)] as string,
            ScrapeGuards = guards,
            CountDropConfirmation = confirmationCategory.HasValue
                ? guards.Single(x => x.Category == confirmationCategory.Value)
                : null,
            GenreMaster = genreMaster
        };
    }

    private async Task<IReadOnlyList<SettingsViewModel.ScrapeGuardStatus>> LoadScrapeGuardsAsync(
        CancellationToken cancellationToken)
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
