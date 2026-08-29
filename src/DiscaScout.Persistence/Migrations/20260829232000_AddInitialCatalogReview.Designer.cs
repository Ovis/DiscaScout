using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist初回Catalogレビュー設定Migrationの識別情報とTargetModelを保持する
/// </summary>
[DbContext(typeof(DiscaScoutDbContext))]
[Migration("20260829232000_AddInitialCatalogReview")]
partial class AddInitialCatalogReview
{
    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        InitialCatalogReviewModelBuilder.Build(modelBuilder);
    }
}
