using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DiscaScout.Web.Pages;

/// <summary>
/// DiscaScoutのアプリケーション設定を編集する画面
/// </summary>
public sealed class SettingsModel(
    DiscordNotificationSettingsStore discordSettingsStore,
    DiscordNotificationService discordNotificationService,
    IScrapeGuardStore scrapeGuardStore,
    IScrapeOperationsStore scrapeOperationsStore,
    IScrapeOperationsQueryStore scrapeOperationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : PageModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
    private static readonly ScrapeCategory[] GuardCategories = [ScrapeCategory.Upcoming, ScrapeCategory.New];

    [BindProperty] public DiscordNotificationMode DiscordMode { get; set; }
    [BindProperty] public string? DiscordWebhookUrl { get; set; }
    [TempData] public string? StatusMessage { get; set; }

    /// <summary>カテゴリごとのスクレイピング安全装置状態</summary>
    public IReadOnlyList<ScrapeGuardStatus> ScrapeGuards { get; private set; } = [];

    /// <summary>急減許可の確認操作中に表示する対象情報</summary>
    public ScrapeGuardStatus? CountDropConfirmation { get; private set; }

    /// <summary>保存済み設定を表示する</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    /// <summary>Discord通知設定をSQLiteへ保存する</summary>
    public async Task<IActionResult> OnPostSaveDiscordAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(DiscordMode)) ModelState.AddModelError(nameof(DiscordMode), "通知モードが不正です");
        if (!string.IsNullOrWhiteSpace(DiscordWebhookUrl)
            && (!Uri.TryCreate(DiscordWebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            ModelState.AddModelError(nameof(DiscordWebhookUrl), "Webhook URLにはHTTPSのURLを指定してください");

        if (!ModelState.IsValid)
        {
            await LoadScrapeGuardsAsync(cancellationToken);
            return Page();
        }
        await discordSettingsStore.UpdateAsync(DiscordMode, DiscordWebhookUrl, cancellationToken);
        StatusMessage = "Discord通知設定を保存しました";
        return RedirectToPage();
    }

    /// <summary>
    /// 保存済みWebhookへテスト通知を送信する
    /// </summary>
    public async Task<IActionResult> OnPostTestDiscordAsync(CancellationToken cancellationToken)
    {
        try
        {
            await discordNotificationService.SendTestAsync(cancellationToken);
            StatusMessage = "Discordへテスト通知を送信しました";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // テスト通知ではWebhook設定ミスを利用者へ返す必要があるため、通常通知と異なり失敗を画面へ表示する。
            StatusMessage = $"Discordへのテスト通知に失敗しました: {ex.Message}";
        }
        return RedirectToPage();
    }

    /// <summary>
    /// 急減許可を有効化する前に対象カテゴリと直近異常値を確認表示する
    /// </summary>
    public async Task<IActionResult> OnPostPrepareCountDropOverrideAsync(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();

        await LoadAsync(cancellationToken);
        CountDropConfirmation = ScrapeGuards.Single(x => x.Category == category);
        return Page();
    }

    /// <summary>
    /// 指定カテゴリの次回1回だけ急減を許可し、そのカテゴリの手動取得をキューへ登録する
    /// </summary>
    public async Task<IActionResult> OnPostEnableCountDropOverrideAsync(
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
            StatusMessage = $"{GetCategoryLabel(category)}の急減を次回1回だけ許可し、確認取得を登録しました";
        }
        else
        {
            // 既に同カテゴリまたはFullScrapeが待機・実行中なら、その取得がOverrideを利用できるため重複アクセスは追加しない。
            StatusMessage = $"{GetCategoryLabel(category)}の急減を次回1回だけ許可しました。既存の通常取得があるため追加の確認取得は登録していません";
        }
        return RedirectToPage();
    }

    /// <summary>指定カテゴリの未消費の急減許可を取り消す</summary>
    public async Task<IActionResult> OnPostCancelCountDropOverrideAsync(
        ScrapeCategory category,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(category)) return BadRequest();

        await scrapeGuardStore.CancelCountDropOverrideAsync(category, cancellationToken);
        StatusMessage = $"{GetCategoryLabel(category)}の急減許可を取り消しました";
        return RedirectToPage();
    }

    /// <summary>UTCで保存した時刻を設定画面用の日本時間へ変換する</summary>
    public static DateTime ToJapanTime(DateTime value)
    {
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, JapanTimeZone);
    }

    /// <summary>スクレイピングカテゴリを日本語表示へ変換する</summary>
    public static string GetCategoryLabel(ScrapeCategory category) => category switch
    {
        ScrapeCategory.Upcoming => "近日リリース",
        ScrapeCategory.New => "新作",
        _ => category.ToString()
    };

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await discordSettingsStore.GetAsync(cancellationToken);
        DiscordMode = settings.Mode;
        DiscordWebhookUrl = settings.WebhookUrl;
        await LoadScrapeGuardsAsync(cancellationToken);
    }

    private async Task LoadScrapeGuardsAsync(CancellationToken cancellationToken)
    {
        var statuses = new List<ScrapeGuardStatus>(GuardCategories.Length);
        foreach (var category in GuardCategories)
        {
            var guard = await scrapeGuardStore.GetAsync(category, cancellationToken);
            var baseline = await scrapeOperationsStore.GetLastAcceptedRunAsync(category, cancellationToken);
            var anomaly = await scrapeOperationsQueryStore.GetLatestAbnormalCountRunAsync(category, cancellationToken);
            statuses.Add(new ScrapeGuardStatus(category, guard, baseline, anomaly));
        }
        ScrapeGuards = statuses;
    }

    /// <summary>
    /// 設定画面で安全装置の現在値と判断材料をまとめて表示する
    /// </summary>
    public sealed record ScrapeGuardStatus(
        ScrapeCategory Category,
        ScrapeGuardSettings Settings,
        ScrapeRun? Baseline,
        ScrapeRun? LatestAnomaly);
}
