namespace DiscaScout.Scraping;

/// <summary>
/// DISCAS検索結果を取得した用途を表す
/// </summary>
public enum DiscSourceCategory
{
    /// <summary>
    /// 近日リリース
    /// </summary>
    Upcoming,

    /// <summary>
    /// 新作
    /// </summary>
    New,

    /// <summary>
    /// Artist全作品収集
    /// </summary>
    ArtistCatalog
}
