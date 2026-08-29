using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <inheritdoc />
public partial class AddScrapeAnomalyGuard : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AbnormalCountReason",
            table: "ScrapeRuns",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "CountDropOverrideUsed",
            table: "ScrapeRuns",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<int>(
            name: "FailureType",
            table: "ScrapeRuns",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "PageCount",
            table: "ScrapeRuns",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Category",
            table: "ManualWorkItems",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ScrapeGuardSettings",
            columns: table => new
            {
                Category = table.Column<int>(type: "INTEGER", nullable: false),
                IsCountDropOverrideEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CountDropOverrideEnabledAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ScrapeGuardSettings", x => x.Category));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ScrapeGuardSettings");

        migrationBuilder.DropColumn(name: "Category", table: "ManualWorkItems");
        migrationBuilder.DropColumn(name: "AbnormalCountReason", table: "ScrapeRuns");
        migrationBuilder.DropColumn(name: "CountDropOverrideUsed", table: "ScrapeRuns");
        migrationBuilder.DropColumn(name: "FailureType", table: "ScrapeRuns");
        migrationBuilder.DropColumn(name: "PageCount", table: "ScrapeRuns");
    }
}
