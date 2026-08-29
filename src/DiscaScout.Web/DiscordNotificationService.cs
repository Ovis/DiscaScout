using System.Net.Http.Json;
using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;

namespace DiscaScout.Web;

/// <summary>
/// 取得処理の結果をDiscord Webhookへ通知する
/// </summary>
public sealed class DiscordNotificationService(IHttpClientFactory httpClientFactory, DiscordNotificationSettingsStore settingsStore, ILogger<DiscordNotificationService> logger)
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>通常カテゴリの取得結果を保存済み通知設定に従って送信する</summary>
    public async Task NotifyScrapeAsync(ScrapeExecutionType executionType, CategoryScrapeResult result, DateTime? nextRetryAt, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings.Mode == DiscordNotificationMode.Off || (result.IsSuccess && settings.Mode != DiscordNotificationMode.SuccessAndFailure)) return;
        var category = result.Category == DiscaScout.Scraping.DiscSourceCategory.Upcoming ? "近日リリース" : "新作";
        var execution = executionType switch { ScrapeExecutionType.Scheduled => "定期取得", ScrapeExecutionType.Manual => "手動取得", ScrapeExecutionType.Retry => "Retry", _ => executionType.ToString() };
        string content;
        if (result.IsSuccess)
            content = $"DiscaScout: {execution} / {category} 成功\n取得 {result.TotalCount ?? 0}件 / 新規 {result.AddedCount}件 / 更新 {result.UpdatedCount}件";
        else
        {
            content = $"DiscaScout: {execution} / {category} 失敗\n{Truncate(result.ErrorMessage, 1200)}";
            if (nextRetryAt.HasValue) content += $"\n次回Retry: {FormatJapanTime(nextRetryAt.Value)}";
        }
        await SendSafelyAsync(settings.WebhookUrl, content, throwOnFailure: false, cancellationToken);
    }

    /// <summary>Artist Catalogの手動取得失敗を通知する</summary>
    public async Task NotifyArtistCatalogFailureAsync(long artistSettingId, string errorMessage, CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (settings.Mode == DiscordNotificationMode.Off) return;
        await SendSafelyAsync(settings.WebhookUrl, $"DiscaScout: Artist全作品収集 失敗\nArtistSettingId: {artistSettingId}\n{Truncate(errorMessage, 1200)}", throwOnFailure: false, cancellationToken);
    }

    /// <summary>
    /// 保存済みWebhookへテスト通知を送信し、設定画面で疎通結果を確認できるようにする
    /// </summary>
    public async Task SendTestAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
            throw new InvalidOperationException("Webhook URLが設定されていません");

        await SendSafelyAsync(settings.WebhookUrl, "DiscaScout: Discord通知のテストです。\nWebhookへの接続に成功しました。", throwOnFailure: true, cancellationToken);
    }

    private async Task SendSafelyAsync(string? webhookUrl, string content, bool throwOnFailure, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;
        try
        {
            using var response = await httpClientFactory.CreateClient("discord-webhook").PostAsJsonAsync(webhookUrl, new { content }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 通常通知ではWebhook障害を本体処理へ波及させないが、手動テストだけは画面へ失敗理由を返す。
            logger.LogWarning(ex, "Discord notification failed");
            if (throwOnFailure) throw;
        }
    }

    private static string FormatJapanTime(DateTime utcDateTime)
    {
        var utc = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, JapanTimeZone).ToString("yyyy-MM-dd HH:mm");
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "詳細なし";
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
