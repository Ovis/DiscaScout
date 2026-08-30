using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCAS CD詳細ページの商品情報と補完メタデータ解析を検証する
/// </summary>
public sealed class DiscasDiscDetailParserTests
{
    private static readonly Uri DetailUri = new(
        "https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=1234567890");

    /// <summary>
    /// 商品名、アーティスト、レンタル開始日、作品詳細、2枚組アイコン、曲目を1ページから取得できることを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPage_ExtractsMetadataAndTracks()
    {
        const string html = """
            <html><body>
              <h1>作品タイトル / テストアーティスト</h1>
              <img src="/library/dis/img/tx_item_info03.png" alt="2枚組">
              <div>レンタル開始日：2026年09月10日</div>
              <h3>作品詳細</h3>
              <p>作品についての説明文です。</p>
              <h3>ジャンル：</h3><div>アニメ</div>
              <h3>曲目 ：</h3>
              <div>1. オープニング (3分59秒)</div>
              <div>2. エンディング (4分04秒)</div>
              <h3>記番：</h3><div>TEST-001</div>
            </body></html>
            """;
        var parser = new DiscasDiscDetailParser();

        var result = parser.Parse(html, DetailUri);

        Assert.Equal("作品タイトル", result.Title);
        Assert.Equal("テストアーティスト", result.Artist);
        Assert.Equal(new DateOnly(2026, 9, 10), result.RentalStartDate);
        Assert.True(result.IsTwoDisc);
        Assert.Equal("作品についての説明文です。", result.Description);
        Assert.Collection(
            result.Tracks,
            track =>
            {
                Assert.Equal(1, track.TrackNumber);
                Assert.Equal("オープニング", track.Title);
                Assert.Equal("3分59秒", track.Duration);
            },
            track =>
            {
                Assert.Equal(2, track.TrackNumber);
                Assert.Equal("エンディング", track.Title);
                Assert.Equal("4分04秒", track.Duration);
            });
    }

    /// <summary>
    /// 商品名自体にスラッシュが含まれても末尾の区切りをアーティスト境界として扱うことを確認する
    /// </summary>
    [Fact]
    public void Parse_TitleContainingSlash_UsesLastSeparatorForArtist()
    {
        const string html = """
            <html><body>
              <h1>ALPHA / BETA / ARTIST</h1>
              <div>レンタル開始日：2026年08月01日</div>
            </body></html>
            """;
        var parser = new DiscasDiscDetailParser();

        var result = parser.Parse(html, DetailUri);

        Assert.Equal("ALPHA / BETA", result.Title);
        Assert.Equal("ARTIST", result.Artist);
    }

    /// <summary>
    /// 2枚組アイコンがない詳細ページは1枚組側としてfalseを返すことを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPageWithoutTwoDiscIcon_ReturnsFalse()
    {
        const string html = """
            <html><body>
              <h1>作品 / アーティスト</h1>
              <div>レンタル開始日：2026年08月01日</div>
              <h3>ジャンル：</h3><div>J-POP</div>
            </body></html>
            """;
        var parser = new DiscasDiscDetailParser();

        var result = parser.Parse(html, DetailUri);

        Assert.False(result.IsTwoDisc);
        Assert.Null(result.Description);
        Assert.Empty(result.Tracks);
    }

    /// <summary>
    /// レンタル開始日の記載がない詳細ページも、商品情報自体を解析できる場合は正常取得として扱うことを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPageWithoutRentalStartDate_ReturnsNullDate()
    {
        const string html = "<html><body><h1>作品 / アーティスト</h1><h3>作品詳細</h3><p>説明のみ</p></body></html>";
        var parser = new DiscasDiscDetailParser();

        var result = parser.Parse(html, DetailUri);

        Assert.Equal("作品", result.Title);
        Assert.Equal("アーティスト", result.Artist);
        Assert.Null(result.RentalStartDate);
    }

    /// <summary>
    /// 商品見出しからタイトルとアーティストを分離できないページを正常取得として扱わないことを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPageWithoutProductHeading_Throws()
    {
        const string html = "<html><body><div>レンタル開始日：2026年08月01日</div></body></html>";
        var parser = new DiscasDiscDetailParser();

        Assert.Throws<DiscasDiscDetailParseException>(() => parser.Parse(html, DetailUri));
    }
}
