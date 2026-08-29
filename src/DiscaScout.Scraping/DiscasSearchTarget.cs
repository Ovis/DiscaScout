using System.Text;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASの検索対象と検索URLを定義する
/// </summary>
public static class DiscasSearchTarget
{
    private static readonly Uri SearchBaseUri = new("https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do");

    static DiscasSearchTarget()
    {
        // DISCASの検索クエリはWindows-31JでURLエンコードされているため、
        // アーティスト名を同じ文字コードで送信できるようコードページを有効化する。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

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

    /// <summary>
    /// 指定アーティスト名をDISCASのアーティスト検索として取得するURLを生成する
    /// </summary>
    /// <param name="artist">検索に送信する表示用アーティスト名</param>
    /// <param name="pageNumber">1から始まるページ番号</param>
    /// <returns>全ジャンルのアーティスト検索結果URL</returns>
    public static Uri CreateArtistUri(string artist, int pageNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artist);
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        // 現行DISCASのアーティスト検索はAK/AKNへ同じ検索語をWindows-31Jで送る。
        // 通常のUri生成ではUTF-8ではない%XX列がQuery正規化で書き換えられるため、
        // DISCASへ送る検索語のバイト列を保持する目的に限ってPath/Queryのcanonicalizationを無効化する。
        var encodedArtist = PercentEncodeWindows31J(artist.Trim());
        var uriString = $"{SearchBaseUri}?AK={encodedArtist}&AKN={encodedArtist}&PA=rt_original_&RT=1&SK=6&SRT=1&PN={pageNumber}";
        var creationOptions = new UriCreationOptions
        {
            DangerousDisablePathAndQueryCanonicalization = true
        };
        return new Uri(uriString, in creationOptions);
    }

    /// <summary>
    /// DISCAS検索フォームと同じWindows-31JでURLクエリ値をエンコードする
    /// </summary>
    private static string PercentEncodeWindows31J(string value)
    {
        var bytes = Encoding.GetEncoding(932).GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);

        foreach (var valueByte in bytes)
        {
            if ((valueByte >= (byte)'A' && valueByte <= (byte)'Z')
                || (valueByte >= (byte)'a' && valueByte <= (byte)'z')
                || (valueByte >= (byte)'0' && valueByte <= (byte)'9')
                || valueByte is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)valueByte);
            }
            else
            {
                builder.Append('%');
                builder.Append(valueByte.ToString("X2"));
            }
        }

        return builder.ToString();
    }
}
