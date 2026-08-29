using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// CD詳細メタデータMigrationの識別情報とTargetModelを保持する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260829223000_AddDiscDetailMetadata")]
partial class AddDiscDetailMetadata
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        DiscDetailMetadataModelBuilder.Build(modelBuilder);
    }
}
