using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist Watch設定とCD一致履歴を保存するテーブルを追加する
/// </summary>
public partial class AddArtistWatch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArtistSettings",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Artist = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                NormalizedArtist = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                MatchType = table.Column<int>(type: "INTEGER", nullable: false),
                IsWatchEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                CollectFullCatalog = table.Column<bool>(type: "INTEGER", nullable: false),
                IsArchived = table.Column<bool>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ArtistSettings", x => x.Id));

        migrationBuilder.CreateTable(
            name: "DiscArtistMatches",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                ArtistSettingId = table.Column<long>(type: "INTEGER", nullable: false),
                IsCurrentMatch = table.Column<bool>(type: "INTEGER", nullable: false),
                FirstMatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastMatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastUnmatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscArtistMatches", x => x.Id);
                table.ForeignKey("FK_DiscArtistMatches_ArtistSettings_ArtistSettingId", x => x.ArtistSettingId, "ArtistSettings", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DiscArtistMatches_Discs_DiscId", x => x.DiscId, "Discs", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_ArtistSettings_IsArchived", "ArtistSettings", "IsArchived");
        migrationBuilder.CreateIndex("IX_ArtistSettings_IsWatchEnabled_IsArchived", "ArtistSettings", new[] { "IsWatchEnabled", "IsArchived" });
        migrationBuilder.CreateIndex("IX_DiscArtistMatches_ArtistSettingId_IsCurrentMatch", "DiscArtistMatches", new[] { "ArtistSettingId", "IsCurrentMatch" });
        migrationBuilder.CreateIndex("IX_DiscArtistMatches_DiscId_ArtistSettingId", "DiscArtistMatches", new[] { "DiscId", "ArtistSettingId" }, unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DiscArtistMatches");
        migrationBuilder.DropTable("ArtistSettings");
    }
}
