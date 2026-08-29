using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 手動バックグラウンド処理キューMigrationの識別情報とTargetModelを保持する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260829210000_AddManualWorkQueue")]
partial class AddManualWorkQueue
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        ManualWorkModelBuilder.Build(modelBuilder);
    }
}
