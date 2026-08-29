using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCAS CD詳細ページの補完メタデータ解析を検証する
/// </summary>
public sealed class DiscasDiscDetailParserTests
{
    private static readonly Uri DetailUri = new(
        "https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=1234567890");

    /// <summary>
    /// レンタル開始日、作品詳細、2枚組アイコン、曲目を1ページから取得できることを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPage_ExtractsMetadataAndTracks()
    {
        const string html = """
            <html><body>
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
    /// 2枚組アイコンがない詳細ページは1枚組側としてfalseを返すことを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPageWithoutTwoDiscIcon_ReturnsFalse()
    {
        const string html = """
            <html><body>
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
    /// 完了判定の基準になるレンタル開始日を取得できないページを正常取得として扱わないことを確認する
    /// </summary>
    [Fact]
    public void Parse_DetailPageWithoutRentalStartDate_Throws()
    {
        const string html = "<html><body><h3>作品詳細</h3><p>説明のみ</p></body></html>";
        var parser = new DiscasDiscDetailParser();

        Assert.Throws<DiscasDiscDetailParseException>(() => parser.Parse(html, DetailUri));
    }
}
