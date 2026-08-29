using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// CD詳細メタデータ追加後の最新SQLiteモデルを構築する
/// </summary>
internal static class DiscDetailMetadataModelBuilder
{
    /// <summary>
    /// 手動処理キュー時点の固定モデルへ詳細メタデータと曲目を追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        ManualWorkModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.Property<string>("Description").HasColumnType("TEXT");
            b.Property<DateTime?>("DetailFetchedAt").HasColumnType("TEXT");
            b.Property<DateTime?>("DetailLastAttemptAt").HasColumnType("TEXT");
            b.Property<bool>("DetailRefreshCompleted").HasColumnType("INTEGER");
            b.Property<bool>("IsMaxiSingle").HasColumnType("INTEGER");
            b.Property<bool?>("IsTwoDisc").HasColumnType("INTEGER");
            b.HasIndex("DetailRefreshCompleted", "DetailFetchedAt");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscTrack", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<string>("Duration").HasMaxLength(100).HasColumnType("TEXT");
            b.Property<int>("TrackNumber").HasColumnType("INTEGER");
            b.Property<string>("Title").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("DiscId", "TrackNumber").IsUnique();
            b.ToTable("DiscTracks");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscTrack", b =>
        {
            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("Tracks")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            b.Navigation("Disc");
        });

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.Navigation("Tracks");
        });
    }
}
