using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// DISCAS詳細ページ由来の高解像度ジャケットURLを保持する列を追加する
/// </summary>
public partial class AddDetailImageUrl : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DetailImageUrl",
            table: "Discs",
            type: "TEXT",
            maxLength: 2048,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DetailImageUrl", table: "Discs");
    }
}
