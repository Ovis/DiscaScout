using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// DISCAS詳細ページ由来の補完メタデータと曲目を保存できるようにする
/// </summary>
public partial class AddDiscDetailMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Description",
            table: "Discs",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "DetailFetchedAt",
            table: "Discs",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "DetailLastAttemptAt",
            table: "Discs",
            type: "TEXT",
            nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "DetailRefreshCompleted",
            table: "Discs",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
        migrationBuilder.AddColumn<bool>(
            name: "IsMaxiSingle",
            table: "Discs",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
        migrationBuilder.AddColumn<bool>(
            name: "IsTwoDisc",
            table: "Discs",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "DiscTracks",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DiscId = table.Column<long>(type: "INTEGER", nullable: false),
                TrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Duration = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DiscTracks", x => x.Id);
                table.ForeignKey(
                    name: "FK_DiscTracks_Discs_DiscId",
                    column: x => x.DiscId,
                    principalTable: "Discs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Discs_DetailRefreshCompleted_DetailFetchedAt",
            table: "Discs",
            columns: new[] { "DetailRefreshCompleted", "DetailFetchedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_DiscTracks_DiscId_TrackNumber",
            table: "DiscTracks",
            columns: new[] { "DiscId", "TrackNumber" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "DiscTracks");
        migrationBuilder.DropIndex(
            name: "IX_Discs_DetailRefreshCompleted_DetailFetchedAt",
            table: "Discs");
        migrationBuilder.DropColumn(name: "Description", table: "Discs");
        migrationBuilder.DropColumn(name: "DetailFetchedAt", table: "Discs");
        migrationBuilder.DropColumn(name: "DetailLastAttemptAt", table: "Discs");
        migrationBuilder.DropColumn(name: "DetailRefreshCompleted", table: "Discs");
        migrationBuilder.DropColumn(name: "IsMaxiSingle", table: "Discs");
        migrationBuilder.DropColumn(name: "IsTwoDisc", table: "Discs");
    }
}
