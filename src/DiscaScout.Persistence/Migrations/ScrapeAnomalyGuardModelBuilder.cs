using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// スクレイピング件数安全装置追加後のSQLiteモデルを構築する
/// </summary>
internal static class ScrapeAnomalyGuardModelBuilder
{
    /// <summary>Discord通知設定追加後の固定モデルへ件数安全装置関連の列と設定テーブルを追加する</summary>
    internal static void Build(ModelBuilder modelBuilder)
    {
        DiscordNotificationSettingsModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.ScrapeRun", b =>
        {
            b.Property<int?>("AbnormalCountReason").HasColumnType("INTEGER");
            b.Property<bool>("CountDropOverrideUsed").HasColumnType("INTEGER");
            b.Property<int>("FailureType").HasColumnType("INTEGER");
            b.Property<int?>("PageCount").HasColumnType("INTEGER");
        });

        modelBuilder.Entity("DiscaScout.Core.ManualWorkItem", b =>
        {
            b.Property<int?>("Category").HasColumnType("INTEGER");
        });

        modelBuilder.Entity("DiscaScout.Core.ScrapeGuardSettings", b =>
        {
            b.Property<int>("Category").ValueGeneratedNever().HasColumnType("INTEGER");
            b.Property<DateTime?>("CountDropOverrideEnabledAt").HasColumnType("TEXT");
            b.Property<bool>("IsCountDropOverrideEnabled").HasColumnType("INTEGER");
            b.HasKey("Category");
            b.ToTable("ScrapeGuardSettings");
        });
    }
}
