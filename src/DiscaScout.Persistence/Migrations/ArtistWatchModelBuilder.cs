using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist Watch追加後の最新SQLiteモデルを構築する
/// </summary>
internal static class ArtistWatchModelBuilder
{
    /// <summary>
    /// InitialCreateの固定モデルへArtist Watch用エンティティを追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        // InitialCreateのTargetModelは将来も不変である必要があるため、初期モデル自体は変更せず、
        // 最新Snapshot側だけで後続Migrationのモデルを積み上げる。
        MigrationModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.ArtistSetting", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<string>("Artist").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<bool>("CollectFullCatalog").HasColumnType("INTEGER");
            b.Property<bool>("IsArchived").HasColumnType("INTEGER");
            b.Property<bool>("IsWatchEnabled").HasColumnType("INTEGER");
            b.Property<int>("MatchType").HasColumnType("INTEGER");
            b.Property<string>("NormalizedArtist").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("IsArchived");
            b.HasIndex("IsWatchEnabled", "IsArchived");
            b.ToTable("ArtistSettings");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscArtistMatch", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<long>("ArtistSettingId").HasColumnType("INTEGER");
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("FirstMatchedAt").HasColumnType("TEXT");
            b.Property<bool>("IsCurrentMatch").HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("LastMatchedAt").HasColumnType("TEXT");
            b.Property<DateTimeOffset?>("LastUnmatchedAt").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("ArtistSettingId", "IsCurrentMatch");
            b.HasIndex("DiscId", "ArtistSettingId").IsUnique();
            b.ToTable("DiscArtistMatches");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscArtistMatch", b =>
        {
            b.HasOne("DiscaScout.Core.ArtistSetting", "ArtistSetting")
                .WithMany("DiscMatches")
                .HasForeignKey("ArtistSettingId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("ArtistMatches")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
    }
}
