namespace DiscaScout.Web;

/// <summary>
/// 手動処理の登録直後にBackgroundServiceを起こすプロセス内シグナル
/// </summary>
public sealed class ManualWorkSignal
{
    private readonly SemaphoreSlim semaphore = new(0, 1);

    /// <summary>
    /// 新しい手動要求が登録されたことを通知する
    /// </summary>
    public void Notify()
    {
        // 複数要求が短時間に登録されてもBackgroundServiceはDBキューを空になるまで処理するため、
        // シグナル自体は1件分だけ保持すれば十分である。
        if (semaphore.CurrentCount == 0)
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// 通知または定期ポーリング期限まで待機する
    /// </summary>
    /// <returns>通知で起床した場合はtrue、タイムアウトの場合はfalse</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => semaphore.WaitAsync(timeout, cancellationToken);
}
