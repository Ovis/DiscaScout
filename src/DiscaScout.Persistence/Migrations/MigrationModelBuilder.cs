using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// MigrationのTargetModelとModelSnapshotで同一のリレーショナルモデル定義を共有する
/// </summary>
internal static class MigrationModelBuilder
{
    /// <summary>
    /// InitialCreate時点のSQLiteモデルを構築する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        // ModelSnapshotはプロバイダーのConventionSetを使わない素のModelBuilderから構築される。
        // DbContextと同じFluent APIだけを書いてもSQLiteの列型やValueGenerated設定が再現されないため、
        // dotnet efが生成するSnapshotと同様にリレーショナル情報を明示する。
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.11")
            .HasAnnotation("Relational:MaxIdentifierLength", 64);

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<string>("Artist").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<string>("DiscasId").IsRequired().HasMaxLength(32).HasColumnType("TEXT");
            b.Property<DateTime>("FirstSeenAt").HasColumnType("TEXT");
            b.Property<string>("GenreLarge").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            b.Property<string>("GenreMiddle").HasMaxLength(200).HasColumnType("TEXT");
            b.Property<string>("GenreSmall").HasMaxLength(200).HasColumnType("TEXT");
            b.Property<string>("ImagePath").HasMaxLength(2048).HasColumnType("TEXT");
            b.Property<string>("ImageUrl").HasMaxLength(2048).HasColumnType("TEXT");
            b.Property<bool>("IsArchived").HasColumnType("INTEGER");
            b.Property<bool>("IsRented").HasColumnType("INTEGER");
            b.Property<DateTime?>("LastReviewedAt").HasColumnType("TEXT");
            b.Property<DateTime>("LastSeenAt").HasColumnType("TEXT");
            b.Property<DateTime>("LastUpdatedAt").HasColumnType("TEXT");
            b.Property<bool>("NeedsReview").HasColumnType("INTEGER");
            b.Property<string>("NormalizedArtist").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<string>("NormalizedTitle").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<string>("ProductUrl").IsRequired().HasMaxLength(2048).HasColumnType("TEXT");
            b.Property<DateOnly?>("RentalStartDate").HasColumnType("TEXT");
            b.Property<string>("Title").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("DiscasId").IsUnique();
            b.HasIndex("GenreLarge");
            b.HasIndex("IsArchived");
            b.HasIndex("IsRented");
            b.HasIndex("NeedsReview");
            b.HasIndex("NormalizedArtist");
            b.HasIndex("NormalizedTitle");
            b.ToTable("Discs");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscChangeHistory", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<DateTime>("ChangedAt").HasColumnType("TEXT");
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<string>("Field").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            b.Property<string>("NewValue").HasColumnType("TEXT");
            b.Property<string>("OldValue").HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("DiscId", "ChangedAt");
            b.ToTable("DiscChangeHistory");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscReviewReason", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<int>("Reason").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("DiscId", "Reason").IsUnique();
            b.ToTable("DiscReviewReasons");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscSource", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<int>("Category").HasColumnType("INTEGER");
            b.Property<long>("DiscId").HasColumnType("INTEGER");
            b.Property<bool>("IsActive").HasColumnType("INTEGER");
            b.Property<DateTime>("LastSeenAt").HasColumnType("TEXT");
            b.Property<int>("MissingCount").HasColumnType("INTEGER");
            b.Property<int>("SourceRank").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("Category", "IsActive", "SourceRank");
            b.HasIndex("DiscId", "Category").IsUnique();
            b.ToTable("DiscSources");
        });

        modelBuilder.Entity("DiscaScout.Core.ScrapeRetry", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<int>("AttemptNumber").HasColumnType("INTEGER");
            b.Property<int>("Category").HasColumnType("INTEGER");
            b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
            b.Property<DateTime>("DueAt").HasColumnType("TEXT");
            b.Property<DateTime?>("ResolvedAt").HasColumnType("TEXT");
            b.Property<int>("Status").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("Category", "Status");
            b.HasIndex("Status", "DueAt");
            b.ToTable("ScrapeRetries");
        });

        modelBuilder.Entity("DiscaScout.Core.ScrapeRun", b =>
        {
            b.Property<long>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER").HasAnnotation("Sqlite:Autoincrement", true);
            b.Property<int>("AddedCount").HasColumnType("INTEGER");
            b.Property<int>("Category").HasColumnType("INTEGER");
            b.Property<DateTime>("CompletedAt").HasColumnType("TEXT");
            b.Property<int>("DeactivatedSourceCount").HasColumnType("INTEGER");
            b.Property<long>("DurationMilliseconds").HasColumnType("INTEGER");
            b.Property<int>("ExecutionType").HasColumnType("INTEGER");
            b.Property<string>("FailureReason").HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<int?>("FetchedCount").HasColumnType("INTEGER");
            b.Property<bool>("IsSuccess").HasColumnType("INTEGER");
            b.Property<int?>("ParsedCount").HasColumnType("INTEGER");
            b.Property<DateTime>("StartedAt").HasColumnType("TEXT");
            b.Property<int>("UpdatedCount").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("Category", "StartedAt");
            b.HasIndex("IsSuccess");
            b.ToTable("ScrapeRuns");
        });

        modelBuilder.Entity("DiscaScout.Core.ScrapeScheduleSettings", b =>
        {
            b.Property<int>("Id").HasColumnType("INTEGER");
            b.Property<int>("DayOfWeek").HasColumnType("INTEGER");
            b.Property<bool>("IsEnabled").HasColumnType("INTEGER");
            b.Property<DateOnly?>("LastScheduledExecutionDate").HasColumnType("TEXT");
            b.Property<TimeOnly>("LocalTime").HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("ScrapeScheduleSettings");
        });

        modelBuilder.Entity("DiscaScout.Core.DiscChangeHistory", b =>
        {
            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("ChangeHistory")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("DiscaScout.Core.DiscReviewReason", b =>
        {
            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("ReviewReasons")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });

        modelBuilder.Entity("DiscaScout.Core.DiscSource", b =>
        {
            b.HasOne("DiscaScout.Core.Disc", "Disc")
                .WithMany("Sources")
                .HasForeignKey("DiscId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
    }
}
