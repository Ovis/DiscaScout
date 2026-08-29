using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist全作品収集の所属関係テーブルを追加する
/// </summary>
public partial class AddArtistCatalog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DiscArtistCatalogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                ArtistSettingId = table.Column<long>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DeactivatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscArtistCatalogs", x => x.Id);
                table.ForeignKey(
                    name: "FK_DiscArtistCatalogs_ArtistSettings_ArtistSettingId",
                    column: x => x.ArtistSettingId,
                    principalTable: "ArtistSettings",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_DiscArtistCatalogs_Discs_DiscId",
                    column: x => x.DiscId,
                    principalTable: "Discs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DiscArtistCatalogs_ArtistSettingId_IsActive",
            table: "DiscArtistCatalogs",
            columns: new[] { "ArtistSettingId", "IsActive" });

        migrationBuilder.CreateIndex(
            name: "IX_DiscArtistCatalogs_DiscId_ArtistSettingId",
            table: "DiscArtistCatalogs",
            columns: new[] { "DiscId", "ArtistSettingId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DiscArtistCatalogs");
    }
}
