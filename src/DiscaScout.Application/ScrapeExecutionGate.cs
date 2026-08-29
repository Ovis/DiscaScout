namespace DiscaScout.Application;

/// <summary>
/// 定期実行・リトライ・手動実行が同時にスクレイピングしないよう全体排他を管理する
/// </summary>
public sealed class ScrapeExecutionGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

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

        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
