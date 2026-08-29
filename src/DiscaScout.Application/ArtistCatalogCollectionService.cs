using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// ArtistSettingを起点にDISCASの全作品検索とSQLite反映を連携する
/// </summary>
public sealed class ArtistCatalogCollectionService(
    IDiscasArtistCatalogCrawler crawler,
    ArtistCatalogStore catalogStore,
    DiscImageCacheService? imageCache = null)
{
    /// <summary>
    /// 指定ArtistSettingの全作品をDISCASから取得し、完全スナップショットとして保存する
    /// </summary>
    /// <param name="artistSettingId">全作品収集対象のArtistSetting ID</param>
    /// <param name="cancellationToken">取得・保存処理を中断するためのトークン</param>
    /// <returns>検索結果件数とCatalog反映件数</returns>
    public async Task<ArtistCatalogApplyResult> CollectAsync(
        long artistSettingId,
        CancellationToken cancellationToken = default)
    {
        var setting = await catalogStore.FindSettingAsync(artistSettingId, cancellationToken)
            ?? throw new InvalidOperationException($"ArtistSettingが存在しない: {artistSettingId}");

        if (setting.IsArchived || !setting.CollectFullCatalog)
        {
            throw new InvalidOperationException("全作品収集が有効なArtistSettingではない");
        }

        // 全ページ取得に成功した場合だけPersistenceへ渡し、途中失敗した検索結果で
        // 既存Catalog関係をInactiveにしないようCrawlerの完全スナップショット契約を維持する。
        var snapshot = await crawler.CrawlAsync(setting.Artist, cancellationToken);
        var result = await catalogStore.ApplyAsync(artistSettingId, snapshot, cancellationToken);
        await TrySyncImagesAsync(snapshot.Products.Select(x => x.DiscasId), cancellationToken);
        return result;
    }

    private async Task TrySyncImagesAsync(IEnumerable<string> discasIds, CancellationToken cancellationToken)
    {
        if (imageCache is null)
        {
            return;
        }

        try
        {
            // Catalog専用CDも同じ画像キャッシュを利用するが、画像はCatalog完全性判定の構成要素ではない。
            // 保存先障害や個別画像障害で、正常に確定したCatalogスナップショットを失敗扱いにしない。
            await imageCache.SyncAsync(discasIds, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ImagePath未更新のCDは次回収集または後続のキャッシュ同期で再試行できる。
        }
    }
}
