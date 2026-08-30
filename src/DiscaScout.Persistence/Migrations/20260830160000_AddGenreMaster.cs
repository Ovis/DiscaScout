using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Discのジャンル文字列3列を廃止し、DISCASジャンルツリーへの外部キーへ置き換える
/// </summary>
public partial class AddGenreMaster : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "GenreMasterStates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false),
                LastUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_GenreMasterStates", x => x.Id));

        migrationBuilder.CreateTable(
            name: "Genres",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                ExternalId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Genres", x => x.Id);
                table.ForeignKey(
                    name: "FK_Genres_Genres_ParentId",
                    column: x => x.ParentId,
                    principalTable: "Genres",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.AddColumn<long>(
            name: "GenreId",
            table: "Discs",
            type: "INTEGER",
            nullable: true);

        // 本番運用前で既存DBは破棄可能なため、旧文字列から推測してマスターを生成する移行処理は行わない。
        // マスターの唯一の正はgenreAll.doとし、次回通常クロール前の初期取得で構築する。
        migrationBuilder.DropColumn(name: "GenreLarge", table: "Discs");
        migrationBuilder.DropColumn(name: "GenreMiddle", table: "Discs");
        migrationBuilder.DropColumn(name: "GenreSmall", table: "Discs");

        migrationBuilder.CreateIndex(name: "IX_Discs_GenreId", table: "Discs", column: "GenreId");
        migrationBuilder.CreateIndex(name: "IX_Genres_ExternalId", table: "Genres", column: "ExternalId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Genres_IsActive", table: "Genres", column: "IsActive");
        migrationBuilder.CreateIndex(name: "IX_Genres_ParentId_SortOrder", table: "Genres", columns: new[] { "ParentId", "SortOrder" });

        migrationBuilder.AddForeignKey(
            name: "FK_Discs_Genres_GenreId",
            table: "Discs",
            column: "GenreId",
            principalTable: "Genres",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(name: "FK_Discs_Genres_GenreId", table: "Discs");
        migrationBuilder.DropTable(name: "GenreMasterStates");
        migrationBuilder.DropTable(name: "Genres");
        migrationBuilder.DropIndex(name: "IX_Discs_GenreId", table: "Discs");
        migrationBuilder.DropColumn(name: "GenreId", table: "Discs");

        migrationBuilder.AddColumn<string>(name: "GenreLarge", table: "Discs", type: "TEXT", maxLength: 200, nullable: false, defaultValue: "未取得");
        migrationBuilder.AddColumn<string>(name: "GenreMiddle", table: "Discs", type: "TEXT", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<string>(name: "GenreSmall", table: "Discs", type: "TEXT", maxLength: 200, nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Discs_GenreLarge", table: "Discs", column: "GenreLarge");
    }
}
