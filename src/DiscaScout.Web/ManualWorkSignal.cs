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
        // シグナル自体は1件分だけ保持すれば十分である。並行Notifyで既に上限へ達した場合も通知済みとして扱う。
        try
        {
            semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
            // 既に未消費の通知が1件あるため追加Releaseは不要
        }
    }

    /// <summary>
    /// 通知または定期ポーリング期限まで待機する
    /// </summary>
    /// <returns>通知で起床した場合はtrue、タイムアウトの場合はfalse</returns>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => semaphore.WaitAsync(timeout, cancellationToken);
}
