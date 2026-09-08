using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 一回限りArtist全作品取得を手動処理キューへ追加した後のSQLiteモデルを構築する
/// </summary>
internal static class OneShotArtistWorkModelBuilder
{
    /// <summary>
    /// 最新固定モデルへ一回限りArtist取得に必要なキュー項目を追加する
    /// </summary>
    /// <param name="modelBuilder">Migration用モデルを構築するModelBuilder</param>
    internal static void Build(ModelBuilder modelBuilder)
    {
        GenreMasterModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.ManualWorkItem", b =>
        {
            b.Property<string>("OneShotArtist").HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<int?>("OneShotMatchType").HasColumnType("INTEGER");
            b.Property<bool>("OneShotReviewNewItems").HasColumnType("INTEGER");
        });
    }
}
