using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// Discord通知設定をSQLiteへ保存する
/// </summary>
public sealed class DiscordNotificationSettingsStore(DiscaScoutDbContext dbContext)
{
    private const int SettingsId = 1;

    /// <summary>現在の通知設定を取得する。未作成なら既定値を返す</summary>
    public async Task<DiscordNotificationSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.DiscordNotificationSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == SettingsId, cancellationToken)
            ?? new DiscordNotificationSettings();
    }

    /// <summary>運用画面から指定された通知設定を保存する</summary>
    public async Task UpdateAsync(DiscordNotificationMode mode, string? webhookUrl, CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.DiscordNotificationSettings.SingleOrDefaultAsync(x => x.Id == SettingsId, cancellationToken);
        if (settings is null)
        {
            settings = new DiscordNotificationSettings { Id = SettingsId };
            dbContext.DiscordNotificationSettings.Add(settings);
        }

        settings.Mode = mode;
        settings.WebhookUrl = string.IsNullOrWhiteSpace(webhookUrl) ? null : webhookUrl.Trim();
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
