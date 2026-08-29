using DiscaScout.Scraping;

namespace DiscaScout.Persistence;

/// <summary>
/// 完全性検証済みのDISCASカテゴリスナップショットを永続化する処理の契約
/// </summary>
public interface IDiscasSnapshotStore
{
    /// <summary>
    /// 指定されたカテゴリスナップショットを永続化する
    /// </summary>
    /// <param name="snapshot">全ページ取得と整合性検証に成功したカテゴリスナップショット</param>
    /// <param name="cancellationToken">永続化処理を中断するためのトークン</param>
    /// <returns>今回の反映件数</returns>
    Task<SnapshotApplyResult> ApplyAsync(
        DiscasCategorySnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="DiscasSnapshotApplier"/> を通常実行フローから利用するための永続化アダプター
/// </summary>
public sealed class DiscasSnapshotStore(DiscasSnapshotApplier applier) : IDiscasSnapshotStore
{
    /// <inheritdoc />
    public Task<SnapshotApplyResult> ApplyAsync(
        DiscasCategorySnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        return applier.ApplyAsync(snapshot, cancellationToken);
    }
}
