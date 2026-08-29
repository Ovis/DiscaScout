using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist全作品収集の初回レビュー設定と初回完了状態を追加する
/// </summary>
public partial class AddInitialCatalogReview : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "InitialCatalogCollectionCompleted", table: "ArtistSettings", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "ReviewInitialCatalogItems", table: "ArtistSettings", type: "INTEGER", nullable: false, defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "InitialCatalogCollectionCompleted", table: "ArtistSettings");
        migrationBuilder.DropColumn(name: "ReviewInitialCatalogItems", table: "ArtistSettings");
    }
}
