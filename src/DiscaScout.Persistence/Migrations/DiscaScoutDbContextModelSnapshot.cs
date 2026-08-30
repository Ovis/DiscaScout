using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 最新のEF Coreモデルを固定し、後続Migrationとの差分基準として使用する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
public sealed class DiscaScoutDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        DetailImageUrlModelBuilder.Build(modelBuilder);
    }
}
