using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Migrations;

/// <summary>
/// 詳細画像URL追加後のSQLiteモデルを構築する
/// </summary>
internal static class DetailImageUrlModelBuilder
{
    /// <summary>
    /// レンタル履歴インポート対応後の固定モデルへ詳細画面用ジャケットURLを追加する
    /// </summary>
    internal static void Build(ModelBuilder modelBuilder)
    {
        RentalHistoryImportModelBuilder.Build(modelBuilder);

        modelBuilder.Entity("DiscaScout.Core.Disc", b =>
        {
            b.Property<string>("DetailImageUrl").HasMaxLength(2048).HasColumnType("TEXT");
        });
    }
}
