using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// InitialCreate時点のEF Coreモデルを固定し、後続Migrationとの差分基準として使用する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
public sealed class DiscaScoutDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.11");

        modelBuilder.Entity<Disc>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DiscasId).HasMaxLength(32);
            entity.Property(x => x.ProductUrl).HasMaxLength(2048);
            entity.Property(x => x.Title).HasMaxLength(1000);
            entity.Property(x => x.NormalizedTitle).HasMaxLength(1000);
            entity.Property(x => x.Artist).HasMaxLength(1000);
            entity.Property(x => x.NormalizedArtist).HasMaxLength(1000);
            entity.Property(x => x.GenreLarge).HasMaxLength(200);
            entity.Property(x => x.GenreMiddle).HasMaxLength(200);
            entity.Property(x => x.GenreSmall).HasMaxLength(200);
            entity.Property(x => x.ImageUrl).HasMaxLength(2048);
            entity.Property(x => x.ImagePath).HasMaxLength(2048);
            entity.HasIndex(x => x.DiscasId).IsUnique();
            entity.HasIndex(x => x.NeedsReview);
            entity.HasIndex(x => x.IsArchived);
            entity.HasIndex(x => x.IsRented);
            entity.HasIndex(x => x.NormalizedTitle);
            entity.HasIndex(x => x.NormalizedArtist);
            entity.HasIndex(x => x.GenreLarge);
        });

        modelBuilder.Entity<DiscSource>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DiscId, x.Category }).IsUnique();
            entity.HasIndex(x => new { x.Category, x.IsActive, x.SourceRank });
            entity.HasOne(x => x.Disc)
                .WithMany(x => x.Sources)
                .HasForeignKey(x => x.DiscId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DiscReviewReason>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.DiscId, x.Reason }).IsUnique();
            entity.HasOne(x => x.Disc)
                .WithMany(x => x.ReviewReasons)
                .HasForeignKey(x => x.DiscId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DiscChangeHistory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Field).HasMaxLength(100);
            entity.HasIndex(x => new { x.DiscId, x.ChangedAt });
            entity.HasOne(x => x.Disc)
                .WithMany(x => x.ChangeHistory)
                .HasForeignKey(x => x.DiscId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScrapeRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FailureReason).HasMaxLength(1000);
            entity.HasIndex(x => new { x.Category, x.StartedAt });
            entity.HasIndex(x => x.IsSuccess);
        });

        modelBuilder.Entity<ScrapeRetry>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.DueAt });
            entity.HasIndex(x => new { x.Category, x.Status });
        });

        modelBuilder.Entity<ScrapeScheduleSettings>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
