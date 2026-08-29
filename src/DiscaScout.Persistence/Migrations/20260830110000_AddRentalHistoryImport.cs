using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRentalHistoryImport : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "RentalHistoryImportedAt",
            table: "Discs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Discs_RentalHistoryImportedAt",
            table: "Discs",
            column: "RentalHistoryImportedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Discs_RentalHistoryImportedAt", table: "Discs");
        migrationBuilder.DropColumn(name: "RentalHistoryImportedAt", table: "Discs");
    }
}
