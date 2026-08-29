using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DiscaScout.Web.Pages;

/// <summary>
/// DiscaScoutのアプリケーション設定を編集する画面
/// </summary>
public sealed class SettingsModel(DiscordNotificationSettingsStore discordSettingsStore, DiscordNotificationService discordNotificationService) : PageModel
{
    [BindProperty] public DiscordNotificationMode DiscordMode { get; set; }
    [BindProperty] public string? DiscordWebhookUrl { get; set; }
    [TempData] public string? StatusMessage { get; set; }

    /// <summary>保存済み設定を表示する</summary>
    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    /// <summary>Discord通知設定をSQLiteへ保存する</summary>
    public async Task<IActionResult> OnPostSaveDiscordAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(DiscordMode)) ModelState.AddModelError(nameof(DiscordMode), "通知モードが不正です");
        if (!string.IsNullOrWhiteSpace(DiscordWebhookUrl)
            && (!Uri.TryCreate(DiscordWebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            ModelState.AddModelError(nameof(DiscordWebhookUrl), "Webhook URLにはHTTPSのURLを指定してください");

        if (!ModelState.IsValid) return Page();
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

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await discordSettingsStore.GetAsync(cancellationToken);
        DiscordMode = settings.Mode;
        DiscordWebhookUrl = settings.WebhookUrl;
    }
}
