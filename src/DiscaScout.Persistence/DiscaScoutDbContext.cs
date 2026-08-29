using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// DiscaScoutのSQLite永続化モデルを管理するEF Core DbContext
/// </summary>
public sealed class DiscaScoutDbContext(DbContextOptions<DiscaScoutDbContext> options) : DbContext(options)
{
    public DbSet<Disc> Discs => Set<Disc>();
    public DbSet<DiscSource> DiscSources => Set<DiscSource>();
    public DbSet<DiscReviewReason> DiscReviewReasons => Set<DiscReviewReason>();
    public DbSet<DiscChangeHistory> DiscChangeHistory => Set<DiscChangeHistory>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();
    public DbSet<ScrapeRetry> ScrapeRetries => Set<ScrapeRetry>();
    public DbSet<ScrapeScheduleSettings> ScrapeScheduleSettings => Set<ScrapeScheduleSettings>();

    /// <summary>
    /// ドメインモデルとSQLiteテーブルの制約・インデックスを構成する
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var disc = modelBuilder.Entity<Disc>();
        disc.HasKey(x => x.Id);
        disc.HasIndex(x => x.DiscasId).IsUnique();
        disc.Property(x => x.DiscasId).HasMaxLength(32);
        disc.Property(x => x.ProductUrl).HasMaxLength(2048);
        disc.Property(x => x.Title).HasMaxLength(1000);
        disc.Property(x => x.NormalizedTitle).HasMaxLength(1000);
        disc.Property(x => x.Artist).HasMaxLength(1000);
        disc.Property(x => x.NormalizedArtist).HasMaxLength(1000);
        disc.Property(x => x.GenreLarge).HasMaxLength(200);
        disc.Property(x => x.GenreMiddle).HasMaxLength(200);
        disc.Property(x => x.GenreSmall).HasMaxLength(200);
        disc.Property(x => x.ImageUrl).HasMaxLength(2048);
        disc.Property(x => x.ImagePath).HasMaxLength(2048);
        disc.HasIndex(x => x.NeedsReview);
        disc.HasIndex(x => x.IsArchived);
        disc.HasIndex(x => x.IsRented);
        disc.HasIndex(x => x.NormalizedTitle);
        disc.HasIndex(x => x.NormalizedArtist);
        disc.HasIndex(x => x.GenreLarge);

        var source = modelBuilder.Entity<DiscSource>();
        source.HasKey(x => x.Id);
        source.HasIndex(x => new { x.DiscId, x.Category }).IsUnique();
        source.HasIndex(x => new { x.Category, x.IsActive, x.SourceRank });
        source.HasOne(x => x.Disc)
            .WithMany(x => x.Sources)
            .HasForeignKey(x => x.DiscId)
            .OnDelete(DeleteBehavior.Cascade);

        var reviewReason = modelBuilder.Entity<DiscReviewReason>();
        reviewReason.HasKey(x => x.Id);
        reviewReason.HasIndex(x => new { x.DiscId, x.Reason }).IsUnique();
        reviewReason.HasOne(x => x.Disc)
            .WithMany(x => x.ReviewReasons)
            .HasForeignKey(x => x.DiscId)
            .OnDelete(DeleteBehavior.Cascade);

        var changeHistory = modelBuilder.Entity<DiscChangeHistory>();
        changeHistory.HasKey(x => x.Id);
        changeHistory.Property(x => x.Field).HasMaxLength(100);
        changeHistory.HasIndex(x => new { x.DiscId, x.ChangedAt });
        changeHistory.HasOne(x => x.Disc)
            .WithMany(x => x.ChangeHistory)
            .HasForeignKey(x => x.DiscId)
            .OnDelete(DeleteBehavior.Cascade);

        var scrapeRun = modelBuilder.Entity<ScrapeRun>();
        scrapeRun.HasKey(x => x.Id);
        scrapeRun.Property(x => x.FailureReason).HasMaxLength(1000);
        scrapeRun.HasIndex(x => new { x.Category, x.StartedAt });
        scrapeRun.HasIndex(x => x.IsSuccess);

        var scrapeRetry = modelBuilder.Entity<ScrapeRetry>();
        scrapeRetry.HasKey(x => x.Id);
        scrapeRetry.HasIndex(x => new { x.Status, x.DueAt });
        scrapeRetry.HasIndex(x => new { x.Category, x.Status });

        var schedule = modelBuilder.Entity<ScrapeScheduleSettings>();
        schedule.HasKey(x => x.Id);
        schedule.Property(x => x.Id).ValueGeneratedNever();
    }
}
