using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Application;

/// <summary>
/// Artist設定を保存せず、一回限りのDISCAS全作品検索とCD取り込みを連携する
/// </summary>
public sealed class OneShotArtistCatalogCollectionService(
    IDiscasArtistCatalogCrawler crawler,
    ArtistCatalogStore catalogStore)
{
    /// <summary>
    /// 指定アーティストの全作品を一回だけ取得し、Artist設定を作らずCDへ反映する
    /// </summary>
    /// <param name="artist">DISCASで検索するアーティスト名</param>
    /// <param name="matchType">取得結果のアーティスト表記に対する一致方法</param>
    /// <param name="reviewNewItems">今回新規登録したCDを未チェックとして保持するか</param>
    public async Task<OneShotArtistCatalogApplyResult> CollectAsync(
        string artist,
        ArtistMatchType matchType,
        bool reviewNewItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);

        // 検索結果全体は既存のArtist Catalog crawlerを再利用し、永続化だけを一回限り用の経路へ分ける。
        var snapshot = await crawler.CrawlAsync(artist.Trim(), cancellationToken);
        return await catalogStore.ApplyOneShotAsync(
            artist.Trim(),
            matchType,
            snapshot,
            reviewNewItems,
            cancellationToken);
    }
}
