using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Discord通知設定追加後のSQLiteモデルを構築する
/// </summary>
internal static class DiscordNotificationSettingsModelBuilder
{
    /// <summary>直前の固定モデルへDiscord通知設定を追加する</summary>
    internal static void Build(ModelBuilder modelBuilder)
    {
        InitialCatalogReviewModelBuilder.Build(modelBuilder);
        modelBuilder.Entity("DiscaScout.Core.DiscordNotificationSettings", b =>
        {
            b.Property<int>("Id").ValueGeneratedNever().HasColumnType("INTEGER");
            b.Property<int>("Mode").HasColumnType("INTEGER");
            b.Property<string>("WebhookUrl").HasMaxLength(2048).HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("DiscordNotificationSettings");
        });
    }
}
