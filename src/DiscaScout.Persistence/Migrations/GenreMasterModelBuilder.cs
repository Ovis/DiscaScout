using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// ジャンルマスター導入後のSQLiteモデルを構築する
/// </summary>
internal static class GenreMasterModelBuilder
{
    /// <summary>
    /// 既存の最新固定モデルへジャンルツリーとDiscのGenreId参照を追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        DetailImageUrlModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            // 旧3列はこのMigrationで廃止するため、過去モデルビルダーから継承したシャドウプロパティを明示的に除外する。
            b.Ignore("GenreLarge");
            b.Ignore("GenreMiddle");
            b.Ignore("GenreSmall");
            b.Property<long?>("GenreId").HasColumnType("INTEGER");
            b.HasIndex("GenreId");
        });

        modelBuilder.Entity("DiscaScout.Core.Genre", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<string>("ExternalId").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            b.Property<DateTime>("FirstSeenAt").HasColumnType("TEXT");
            b.Property<bool>("IsActive").HasColumnType("INTEGER");
            b.Property<DateTime>("LastSeenAt").HasColumnType("TEXT");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            b.Property<long?>("ParentId").HasColumnType("INTEGER");
            b.Property<int>("SortOrder").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("ExternalId").IsUnique();
            b.HasIndex("IsActive");
            b.HasIndex("ParentId", "SortOrder");
            b.ToTable("Genres");
        });

        modelBuilder.Entity("DiscaScout.Core.GenreMasterState", b =>
        {
            b.Property<int>("Id").HasColumnType("INTEGER");
            b.Property<DateTime?>("LastUpdatedAt").HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("GenreMasterStates");
        });

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.HasOne("DiscaScout.Core.Genre", "Genre")
                .WithMany("Discs")
                .HasForeignKey("GenreId")
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity("DiscaScout.Core.Genre", b =>
        {
            b.HasOne("DiscaScout.Core.Genre", "Parent")
                .WithMany("Children")
                .HasForeignKey("ParentId")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
