namespace DiscaScout.Application;

/// <summary>
/// 定期実行・リトライ・手動実行が同時にスクレイピングしないよう全体排他を管理する
/// </summary>
public sealed class ScrapeExecutionGate
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int isRunning;

    /// <summary>
    /// 通常カテゴリ取得またはArtist Catalog取得が現在実行中かどうかを返す
    /// </summary>
    /// <remarks>
    /// 詳細メタデータ補完は通常クロールを遅くしないことを優先するため、この状態を見て自主的に待機する。
    /// 実際のDISCAS HTTP排他は別途DiscasRequestThrottleが保証する。
    /// </remarks>
    public bool IsRunning => Volatile.Read(ref isRunning) != 0;

    /// <summary>
    /// 他のスクレイピングが動いていない場合だけ処理を開始する
    /// </summary>
    /// <typeparam name="T">実行結果の型</typeparam>
    /// <param name="action">排他区間内で実行する処理</param>
    /// <param name="cancellationToken">待機と実行を中断するためのトークン</param>
    /// <returns>開始できた場合は結果、既に実行中の場合はnull</returns>
    public async Task<T?> TryRunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(action);

        // Webの手動実行では「待たせる」のではなく実行中であることを即時表示したいため、
        // SemaphoreSlimを待機せず取得し、取得できなければ呼び出し元へbusyを返す。
        if (!await gate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return null;
        }

        Volatile.Write(ref isRunning, 1);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
            gate.Release();
        }
    }
}
