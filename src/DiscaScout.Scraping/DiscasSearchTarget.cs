namespace DiscaScout.Scraping;

/// <summary>
/// DISCASの通常収集対象カテゴリと検索URLを定義する
/// </summary>
public static class DiscasSearchTarget
{
    private static readonly Uri SearchBaseUri = new("https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do");

    /// <summary>
    /// 指定カテゴリの検索結果URLを生成する
    /// </summary>
    /// <param name="category">取得対象のリリースカテゴリ</param>
    /// <param name="pageNumber">1から始まるページ番号</param>
    /// <returns>全ジャンルをレンタル開始日の新しい順で取得する検索結果URL</returns>
    public static Uri CreateUri(DiscSourceCategory category, int pageNumber)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        var searchKey = category switch
        {
            DiscSourceCategory.Upcoming => "discas_music_soon",
            DiscSourceCategory.New => "discas_music_new",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };

        // Gを指定すると特定ジャンルだけに絞り込まれるため、通常収集では意図的に付与しない。
        // DiscaScoutは新作・近日リリースの全ジャンルを観測することが目的であり、ジャンル別クロールは不要である。
        var query = $"PA=g_sk_&PN={pageNumber}&SK={searchKey}&SRT=5";
        return new UriBuilder(SearchBaseUri) { Query = query }.Uri;
    }
}
