using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>
/// DiscaScoutのSQLite永続化モデルを管理するEF Core DbContext
/// </summary>
public sealed class DiscaScoutDbContext(DbContextOptions<DiscaScoutDbContext> options) : DbContext(options)
{
    public DbSet<Disc> Discs => Set<Disc>();
    public DbSet<DiscTrack> DiscTracks => Set<DiscTrack>();
    public DbSet<DiscSource> DiscSources => Set<DiscSource>();
    public DbSet<DiscReviewReason> DiscReviewReasons => Set<DiscReviewReason>();
    public DbSet<DiscChangeHistory> DiscChangeHistory => Set<DiscChangeHistory>();
    public DbSet<ArtistSetting> ArtistSettings => Set<ArtistSetting>();
    public DbSet<DiscArtistMatch> DiscArtistMatches => Set<DiscArtistMatch>();
    public DbSet<DiscArtistCatalog> DiscArtistCatalogs => Set<DiscArtistCatalog>();
    public DbSet<ScrapeRun> ScrapeRuns => Set<ScrapeRun>();
    public DbSet<ScrapeRetry> ScrapeRetries => Set<ScrapeRetry>();
    public DbSet<ScrapeScheduleSettings> ScrapeScheduleSettings => Set<ScrapeScheduleSettings>();
    public DbSet<ScrapeGuardSettings> ScrapeGuardSettings => Set<ScrapeGuardSettings>();
    public DbSet<ManualWorkItem> ManualWorkItems => Set<ManualWorkItem>();
    public DbSet<DiscordNotificationSettings> DiscordNotificationSettings => Set<DiscordNotificationSettings>();

    /// <summary>ドメインモデルとSQLiteテーブルの制約・インデックスを構成する</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var disc = modelBuilder.Entity<Disc>();
        disc.HasKey(x => x.Id); disc.HasIndex(x => x.DiscasId).IsUnique();
        disc.Property(x => x.DiscasId).HasMaxLength(32); disc.Property(x => x.ProductUrl).HasMaxLength(2048);
        disc.Property(x => x.Title).HasMaxLength(1000); disc.Property(x => x.NormalizedTitle).HasMaxLength(1000);
        disc.Property(x => x.Artist).HasMaxLength(1000); disc.Property(x => x.NormalizedArtist).HasMaxLength(1000);
        disc.Property(x => x.GenreLarge).HasMaxLength(200); disc.Property(x => x.GenreMiddle).HasMaxLength(200); disc.Property(x => x.GenreSmall).HasMaxLength(200);
        disc.Property(x => x.ImageUrl).HasMaxLength(2048); disc.Property(x => x.ImagePath).HasMaxLength(2048);
        disc.HasIndex(x => x.NeedsReview); disc.HasIndex(x => x.IsArchived); disc.HasIndex(x => x.IsRented);
        disc.HasIndex(x => x.NormalizedTitle); disc.HasIndex(x => x.NormalizedArtist); disc.HasIndex(x => x.GenreLarge);
        disc.HasIndex(x => new { x.DetailRefreshCompleted, x.DetailFetchedAt });
        disc.HasIndex(x => x.RentalHistoryImportedAt);

        var track = modelBuilder.Entity<DiscTrack>();
        track.HasKey(x => x.Id); track.Property(x => x.Title).HasMaxLength(1000); track.Property(x => x.Duration).HasMaxLength(100);
        track.HasIndex(x => new { x.DiscId, x.TrackNumber }).IsUnique();
        track.HasOne(x => x.Disc).WithMany(x => x.Tracks).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade);

        var source = modelBuilder.Entity<DiscSource>(); source.HasKey(x => x.Id); source.HasIndex(x => new { x.DiscId, x.Category }).IsUnique(); source.HasIndex(x => new { x.Category, x.IsActive, x.SourceRank }); source.HasOne(x => x.Disc).WithMany(x => x.Sources).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade);
        var reviewReason = modelBuilder.Entity<DiscReviewReason>(); reviewReason.HasKey(x => x.Id); reviewReason.HasIndex(x => new { x.DiscId, x.Reason }).IsUnique(); reviewReason.HasOne(x => x.Disc).WithMany(x => x.ReviewReasons).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade);
        var changeHistory = modelBuilder.Entity<DiscChangeHistory>(); changeHistory.HasKey(x => x.Id); changeHistory.Property(x => x.Field).HasMaxLength(100); changeHistory.HasIndex(x => new { x.DiscId, x.ChangedAt }); changeHistory.HasOne(x => x.Disc).WithMany(x => x.ChangeHistory).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade);

        var artistSetting = modelBuilder.Entity<ArtistSetting>(); artistSetting.HasKey(x => x.Id); artistSetting.Property(x => x.Artist).HasMaxLength(1000); artistSetting.Property(x => x.NormalizedArtist).HasMaxLength(1000); artistSetting.HasIndex(x => x.IsArchived); artistSetting.HasIndex(x => new { x.IsWatchEnabled, x.IsArchived });
        var artistMatch = modelBuilder.Entity<DiscArtistMatch>(); artistMatch.HasKey(x => x.Id); artistMatch.HasIndex(x => new { x.DiscId, x.ArtistSettingId }).IsUnique(); artistMatch.HasIndex(x => new { x.ArtistSettingId, x.IsCurrentMatch }); artistMatch.HasOne(x => x.Disc).WithMany(x => x.ArtistMatches).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade); artistMatch.HasOne(x => x.ArtistSetting).WithMany(x => x.DiscMatches).HasForeignKey(x => x.ArtistSettingId).OnDelete(DeleteBehavior.Cascade);
        var artistCatalog = modelBuilder.Entity<DiscArtistCatalog>(); artistCatalog.HasKey(x => x.Id); artistCatalog.HasIndex(x => new { x.DiscId, x.ArtistSettingId }).IsUnique(); artistCatalog.HasIndex(x => new { x.ArtistSettingId, x.IsActive }); artistCatalog.HasOne(x => x.Disc).WithMany(x => x.ArtistCatalogEntries).HasForeignKey(x => x.DiscId).OnDelete(DeleteBehavior.Cascade); artistCatalog.HasOne(x => x.ArtistSetting).WithMany(x => x.CatalogEntries).HasForeignKey(x => x.ArtistSettingId).OnDelete(DeleteBehavior.Cascade);

        var scrapeRun = modelBuilder.Entity<ScrapeRun>(); scrapeRun.HasKey(x => x.Id); scrapeRun.Property(x => x.FailureReason).HasMaxLength(1000); scrapeRun.HasIndex(x => new { x.Category, x.StartedAt }); scrapeRun.HasIndex(x => x.IsSuccess);
        var scrapeRetry = modelBuilder.Entity<ScrapeRetry>(); scrapeRetry.HasKey(x => x.Id); scrapeRetry.HasIndex(x => new { x.Status, x.DueAt }); scrapeRetry.HasIndex(x => new { x.Category, x.Status });
        var schedule = modelBuilder.Entity<ScrapeScheduleSettings>(); schedule.HasKey(x => x.Id); schedule.Property(x => x.Id).ValueGeneratedNever();

        // 安全装置はカテゴリごとに1行だけ保持し、明示的な急減許可が再起動をまたいでも失われないようにする。
        var scrapeGuard = modelBuilder.Entity<ScrapeGuardSettings>(); scrapeGuard.HasKey(x => x.Category); scrapeGuard.Property(x => x.Category).ValueGeneratedNever();

        var manualWork = modelBuilder.Entity<ManualWorkItem>(); manualWork.HasKey(x => x.Id); manualWork.Property(x => x.FailureReason).HasMaxLength(1000); manualWork.HasIndex(x => new { x.Status, x.RequestedAt }); manualWork.HasIndex(x => new { x.Type, x.Status }); manualWork.HasIndex(x => new { x.ArtistSettingId, x.Status });

        // Webhook URLは運用画面から変更する単一設定なので固定IDの1行として保持する。
        var discord = modelBuilder.Entity<DiscordNotificationSettings>();
        discord.HasKey(x => x.Id); discord.Property(x => x.Id).ValueGeneratedNever(); discord.Property(x => x.WebhookUrl).HasMaxLength(2048);
    }
}
