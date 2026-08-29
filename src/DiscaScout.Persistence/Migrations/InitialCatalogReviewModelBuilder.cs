using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// Artist全作品収集の初回レビュー設定追加後のSQLiteモデルを構築する
/// </summary>
internal static class InitialCatalogReviewModelBuilder
{
    /// <summary>
    /// CD詳細メタデータ時点の固定モデルへ初回Catalogレビュー状態を追加する
    /// </summary>
    internal static void Build(ModelBuilder modelBuilder)
    {
        DiscDetailMetadataModelBuilder.Build(modelBuilder);
        modelBuilder.Entity("DiscaScout.Core.ArtistSetting", b =>
        {
            b.Property<bool>("InitialCatalogCollectionCompleted").HasColumnType("INTEGER");
            b.Property<bool>("ReviewInitialCatalogItems").HasColumnType("INTEGER");
        });
    }
}
