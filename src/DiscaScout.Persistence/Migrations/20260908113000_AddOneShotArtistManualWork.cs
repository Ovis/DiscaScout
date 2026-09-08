using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 一回限りArtist全作品取得を再起動可能な手動処理キューとして保持する列を追加する
/// </summary>
public partial class AddOneShotArtistManualWork : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "OneShotArtist", table: "ManualWorkItems", type: "TEXT", maxLength: 1000, nullable: true);
        migrationBuilder.AddColumn<int>(name: "OneShotMatchType", table: "ManualWorkItems", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "OneShotReviewNewItems", table: "ManualWorkItems", type: "INTEGER", nullable: false, defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "OneShotArtist", table: "ManualWorkItems");
        migrationBuilder.DropColumn(name: "OneShotMatchType", table: "ManualWorkItems");
        migrationBuilder.DropColumn(name: "OneShotReviewNewItems", table: "ManualWorkItems");
    }
}
