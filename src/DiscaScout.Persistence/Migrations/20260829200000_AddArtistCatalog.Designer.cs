using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// AddArtistCatalog Migrationの識別情報とTargetModelを保持する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260829200000_AddArtistCatalog")]
partial class AddArtistCatalog
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        ArtistCatalogModelBuilder.Build(modelBuilder);
    }
}
