using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// ArtistSettingを起点にDISCASの全作品検索とSQLite反映を連携する
/// </summary>
public sealed class ArtistCatalogCollectionService(
    IDiscasArtistCatalogCrawler crawler,
    ArtistCatalogStore catalogStore)
{
    /// <summary>
    /// 指定ArtistSettingの全作品をDISCASから取得し、完全スナップショットとして保存する
    /// </summary>
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

        // 画像取得は専用BackgroundServiceへ分離し、Catalogの完全性判定を検索HTMLとSQLite反映だけで完結させる。
        var snapshot = await crawler.CrawlAsync(setting.Artist, cancellationToken);
        return await catalogStore.ApplyAsync(artistSettingId, snapshot, cancellationToken);
    }
}
