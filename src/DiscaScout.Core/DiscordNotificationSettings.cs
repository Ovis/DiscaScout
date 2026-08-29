namespace DiscaScout.Core;

/// <summary>
/// Discord Webhook通知の永続設定を保持する
/// </summary>
public sealed class DiscordNotificationSettings
{
    /// <summary>単一設定行として扱うための固定ID</summary>
    public int Id { get; set; } = 1;

    /// <summary>Discord Webhook URL。未設定の場合は通知を送信しない</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>通知する実行結果の範囲</summary>
    public DiscordNotificationMode Mode { get; set; } = DiscordNotificationMode.FailureOnly;
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
