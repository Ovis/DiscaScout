using System.Net.Http.Json;
using DiscaScout.Application;
using DiscaScout.Core;

namespace DiscaScout.Web;

/// <summary>
/// 取得処理の結果をDiscord Webhookへ通知する
/// </summary>
public sealed class DiscordNotificationService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<DiscordNotificationService> logger)
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>
    /// 通常カテゴリの取得結果を設定された通知モードに従って送信する
    /// </summary>
    /// <param name="executionType">定期、手動、Retryの実行種別</param>
    /// <param name="result">カテゴリ単位の取得結果</param>
    /// <param name="nextRetryAt">失敗時に登録された次回Retry予定。最終Retry失敗時はnull</param>
    /// <param name="cancellationToken">アプリケーション停止時に送信を中断するためのトークン</param>
    public Task NotifyScrapeAsync(
        ScrapeExecutionType executionType,
        CategoryScrapeResult result,
        DateTime? nextRetryAt,
        CancellationToken cancellationToken)
    {
        var mode = GetMode();
        if (mode == DiscordNotificationMode.Off || (result.IsSuccess && mode != DiscordNotificationMode.SuccessAndFailure))
        {
            return Task.CompletedTask;
        }

        var category = result.Category == DiscaScout.Scraping.DiscSourceCategory.Upcoming ? "近日リリース" : "新作";
        var execution = executionType switch
        {
            ScrapeExecutionType.Scheduled => "定期取得",
            ScrapeExecutionType.Manual => "手動取得",
            ScrapeExecutionType.Retry => "Retry",
            _ => executionType.ToString()
        };

        string content;
        if (result.IsSuccess)
        {
            content = $"DiscaScout: {execution} / {category} 成功\n"
                + $"取得 {result.TotalCount ?? 0}件 / 新規 {result.AddedCount}件 / 更新 {result.UpdatedCount}件";
        }
        else
        {
            content = $"DiscaScout: {execution} / {category} 失敗\n{Truncate(result.ErrorMessage, 1200)}";
            if (nextRetryAt.HasValue)
            {
                content += $"\n次回Retry: {FormatJapanTime(nextRetryAt.Value)}";
            }
        }

        return SendSafelyAsync(content, cancellationToken);
    }

    /// <summary>
    /// Artist Catalogの手動取得失敗を通知する
    /// </summary>
    public Task NotifyArtistCatalogFailureAsync(long artistSettingId, string errorMessage, CancellationToken cancellationToken)
    {
        if (GetMode() == DiscordNotificationMode.Off)
        {
            return Task.CompletedTask;
        }

        return SendSafelyAsync(
            $"DiscaScout: Artist全作品収集 失敗\nArtistSettingId: {artistSettingId}\n{Truncate(errorMessage, 1200)}",
            cancellationToken);
    }

    private async Task SendSafelyAsync(string content, CancellationToken cancellationToken)
    {
        var webhookUrl = configuration["DiscaScout:Discord:WebhookUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            // Webhook未設定は通知機能を実質無効化する。ローカル開発で毎回設定を要求しないためエラーにはしない。
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient("discord-webhook");
            using var response = await client.PostAsJsonAsync(webhookUrl, new { content }, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Discordは監視経路であり本体処理の依存先ではない。送信失敗で取得結果やRetry制御を変えない。
            logger.LogWarning(ex, "Discord notification failed");
        }
    }

    private DiscordNotificationMode GetMode()
    {
        var value = configuration["DiscaScout:Discord:Mode"];
        return Enum.TryParse<DiscordNotificationMode>(value, ignoreCase: true, out var mode)
            ? mode
            : DiscordNotificationMode.FailureOnly;
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

/// <summary>
/// Discordへ送信する取得結果の範囲を表す
/// </summary>
public enum DiscordNotificationMode
{
    Off,
    FailureOnly,
    SuccessAndFailure
}
