using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist全作品収集追加後の最新SQLiteモデルを構築する
/// </summary>
internal static class ArtistCatalogModelBuilder
{
    /// <summary>
    /// Artist Watch時点の固定モデルへ全作品収集用エンティティを追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        ArtistWatchModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.DiscArtistCatalog", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<long>("ArtistSettingId").HasColumnType("INTEGER");
            b.Property<DateTimeOffset?>("DeactivatedAt").HasColumnType("TEXT");
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("FirstSeenAt").HasColumnType("TEXT");
            b.Property<bool>("IsActive").HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("LastSeenAt").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ArtistSettingId", "IsActive");
            b.HasIndex("DiscId", "ArtistSettingId").IsUnique();
            b.ToTable("DiscArtistCatalogs");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscArtistCatalog", b =>
        {
            b.HasOne("DiscaScout.Core.ArtistSetting", "ArtistSetting")
                .WithMany("CatalogEntries")
                .HasForeignKey("ArtistSettingId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("ArtistCatalogEntries")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
    }
}
