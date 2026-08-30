using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// ジャンルマスター追加MigrationのTargetModelを保持する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260830160000_AddGenreMaster")]
partial class AddGenreMaster
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        GenreMasterModelBuilder.Build(modelBuilder);
    }
}
