using System.Collections.Concurrent;

namespace DiscaScout.Web;

/// <summary>
/// 詳細画面で参照されたCDを詳細メタデータ取得の優先対象としてBackgroundServiceへ通知する
/// </summary>
public sealed class DiscDetailFetchSignal
{
    private readonly ConcurrentQueue<long> queue = new();
    private readonly ConcurrentDictionary<long, byte> queuedIds = new();
    private readonly SemaphoreSlim signal = new(0, 1);

    /// <summary>
    /// 指定CDを優先取得候補へ追加する
    /// </summary>
    /// <param name="discId">Discの内部ID</param>
    public void Request(long discId)
    {
        // 詳細画面の再読み込みで同じIDが大量に積み上がらないよう、キュー内では1件に集約する。
        if (!queuedIds.TryAdd(discId, 0))
        {
            return;
        }

        queue.Enqueue(discId);
        if (signal.CurrentCount == 0)
        {
            signal.Release();
        }
    }

    /// <summary>
    /// 優先取得候補を1件取り出す
    /// </summary>
    public bool TryDequeue(out long discId)
    {
        if (!queue.TryDequeue(out discId))
        {
            return false;
        }

        queuedIds.TryRemove(discId, out _);
        return true;
    }

    /// <summary>
    /// 新しい優先要求が来るか、指定時間が経過するまで待機する
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        await signal.WaitAsync(timeout, cancellationToken);
    }
}
