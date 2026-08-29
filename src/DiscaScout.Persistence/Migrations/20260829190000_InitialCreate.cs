using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// DiscaScoutの初期永続スキーマを作成する
/// </summary>
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Discs",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscasId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                ProductUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Artist = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                NormalizedArtist = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                GenreLarge = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                GenreMiddle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                GenreSmall = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                ImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                ImagePath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                RentalStartDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                NeedsReview = table.Column<bool>(type: "INTEGER", nullable: false),
                LastReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                IsRented = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_Discs", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ScrapeRetries",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Category = table.Column<int>(type: "INTEGER", nullable: false),
                AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                DueAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ScrapeRetries", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ScrapeRuns",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ExecutionType = table.Column<int>(type: "INTEGER", nullable: false),
                Category = table.Column<int>(type: "INTEGER", nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                IsSuccess = table.Column<bool>(type: "INTEGER", nullable: false),
                FetchedCount = table.Column<int>(type: "INTEGER", nullable: true),
                ParsedCount = table.Column<int>(type: "INTEGER", nullable: true),
                AddedCount = table.Column<int>(type: "INTEGER", nullable: false),
                UpdatedCount = table.Column<int>(type: "INTEGER", nullable: false),
                DeactivatedSourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ScrapeRuns", x => x.Id));

        migrationBuilder.CreateTable(
            name: "ScrapeScheduleSettings",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                LocalTime = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                LastScheduledExecutionDate = table.Column<DateOnly>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ScrapeScheduleSettings", x => x.Id));

        migrationBuilder.CreateTable(
            name: "DiscChangeHistory",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                Field = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                OldValue = table.Column<string>(type: "TEXT", nullable: true),
                NewValue = table.Column<string>(type: "TEXT", nullable: true),
                ChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscChangeHistory", x => x.Id);
                table.ForeignKey("FK_DiscChangeHistory_Discs_DiscId", x => x.DiscId, "Discs", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DiscReviewReasons",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                Reason = table.Column<int>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscReviewReasons", x => x.Id);
                table.ForeignKey("FK_DiscReviewReasons_Discs_DiscId", x => x.DiscId, "Discs", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DiscSources",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                Category = table.Column<int>(type: "INTEGER", nullable: false),
                SourceRank = table.Column<int>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                MissingCount = table.Column<int>(type: "INTEGER", nullable: false),
                LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscSources", x => x.Id);
                table.ForeignKey("FK_DiscSources_Discs_DiscId", x => x.DiscId, "Discs", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_Discs_DiscasId", "Discs", "DiscasId", unique: true);
        migrationBuilder.CreateIndex("IX_Discs_GenreLarge", "Discs", "GenreLarge");
        migrationBuilder.CreateIndex("IX_Discs_IsArchived", "Discs", "IsArchived");
        migrationBuilder.CreateIndex("IX_Discs_IsRented", "Discs", "IsRented");
        migrationBuilder.CreateIndex("IX_Discs_NeedsReview", "Discs", "NeedsReview");
        migrationBuilder.CreateIndex("IX_Discs_NormalizedArtist", "Discs", "NormalizedArtist");
        migrationBuilder.CreateIndex("IX_Discs_NormalizedTitle", "Discs", "NormalizedTitle");
        migrationBuilder.CreateIndex("IX_DiscChangeHistory_DiscId_ChangedAt", "DiscChangeHistory", new[] { "DiscId", "ChangedAt" });
        migrationBuilder.CreateIndex("IX_DiscReviewReasons_DiscId_Reason", "DiscReviewReasons", new[] { "DiscId", "Reason" }, unique: true);
        migrationBuilder.CreateIndex("IX_DiscSources_Category_IsActive_SourceRank", "DiscSources", new[] { "Category", "IsActive", "SourceRank" });
        migrationBuilder.CreateIndex("IX_DiscSources_DiscId_Category", "DiscSources", new[] { "DiscId", "Category" }, unique: true);
        migrationBuilder.CreateIndex("IX_ScrapeRetries_Category_Status", "ScrapeRetries", new[] { "Category", "Status" });
        migrationBuilder.CreateIndex("IX_ScrapeRetries_Status_DueAt", "ScrapeRetries", new[] { "Status", "DueAt" });
        migrationBuilder.CreateIndex("IX_ScrapeRuns_Category_StartedAt", "ScrapeRuns", new[] { "Category", "StartedAt" });
        migrationBuilder.CreateIndex("IX_ScrapeRuns_IsSuccess", "ScrapeRuns", "IsSuccess");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DiscChangeHistory");
        migrationBuilder.DropTable("DiscReviewReasons");
        migrationBuilder.DropTable("DiscSources");
        migrationBuilder.DropTable("ScrapeRetries");
        migrationBuilder.DropTable("ScrapeRuns");
        migrationBuilder.DropTable("ScrapeScheduleSettings");
        migrationBuilder.DropTable("Discs");
    }
}
