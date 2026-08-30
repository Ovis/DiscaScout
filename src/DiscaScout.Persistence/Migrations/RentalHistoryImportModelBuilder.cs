using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>レンタル履歴インポート対応後のSQLiteモデルを構築する</summary>
internal static class RentalHistoryImportModelBuilder
{
    /// <summary>件数安全装置追加後の固定モデルへレンタル履歴由来の識別列と詳細画像URLを追加する</summary>
    internal static void Build(ModelBuilder modelBuilder)
    {
        ScrapeAnomalyGuardModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.Property<string>("DetailImageUrl").HasMaxLength(2048).HasColumnType("TEXT");
            b.Property<DateTime?>("RentalHistoryImportedAt").HasColumnType("TEXT");
            b.HasIndex("RentalHistoryImportedAt");
        });
    }
}
