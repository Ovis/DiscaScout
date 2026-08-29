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
        return await catalogStore.ApplyAsync(artistSettingId, snapshot, cancellationToken);
    }
}
