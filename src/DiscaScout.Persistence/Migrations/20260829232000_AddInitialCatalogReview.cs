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

        // このMigration導入前からCatalog関係を持つ設定は既に初回取得済みである。
        // falseのままにすると次回の手動再取得を「初回」と誤認するため、既存関係がある設定だけ完了済みに補正する。
        migrationBuilder.Sql("UPDATE ArtistSettings SET InitialCatalogCollectionCompleted = 1 WHERE Id IN (SELECT DISTINCT ArtistSettingId FROM DiscArtistCatalogs);");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "InitialCatalogCollectionCompleted", table: "ArtistSettings");
        migrationBuilder.DropColumn(name: "ReviewInitialCatalogItems", table: "ArtistSettings");
    }
}
