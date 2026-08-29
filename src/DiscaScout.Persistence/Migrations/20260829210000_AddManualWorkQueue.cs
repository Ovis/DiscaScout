using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Web要求の長時間処理をBackgroundServiceへ渡す永続キューを追加する
/// </summary>
public partial class AddManualWorkQueue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ManualWorkItems",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ArtistSettingId = table.Column<long>(type: "INTEGER", nullable: true),
                RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                FailureReason = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_ManualWorkItems", x => x.Id));

        migrationBuilder.CreateIndex(
            name: "IX_ManualWorkItems_ArtistSettingId_Status",
            table: "ManualWorkItems",
            columns: new[] { "ArtistSettingId", "Status" });
        migrationBuilder.CreateIndex(
            name: "IX_ManualWorkItems_Status_RequestedAt",
            table: "ManualWorkItems",
            columns: new[] { "Status", "RequestedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_ManualWorkItems_Type_Status",
            table: "ManualWorkItems",
            columns: new[] { "Type", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ManualWorkItems");
    }
}
