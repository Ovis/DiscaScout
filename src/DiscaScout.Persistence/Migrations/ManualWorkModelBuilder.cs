using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 手動バックグラウンド処理キュー追加後の最新SQLiteモデルを構築する
/// </summary>
internal static class ManualWorkModelBuilder
{
    /// <summary>
    /// Artist Catalog時点の固定モデルへ手動処理キューを追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        ArtistCatalogModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.ManualWorkItem", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<long?>("ArtistSettingId").HasColumnType("INTEGER");
            b.Property<DateTime?>("CompletedAt").HasColumnType("TEXT");
            b.Property<string>("FailureReason").HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<DateTime>("RequestedAt").HasColumnType("TEXT");
            b.Property<DateTime?>("StartedAt").HasColumnType("TEXT");
            b.Property<int>("Status").HasColumnType("INTEGER");
            b.Property<int>("Type").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("ArtistSettingId", "Status");
            b.HasIndex("Status", "RequestedAt");
            b.HasIndex("Type", "Status");
            b.ToTable("ManualWorkItems");
        });
    }
}
