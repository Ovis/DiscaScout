using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// CD詳細ページのジャンル階層解析を検証する
/// </summary>
public sealed class DiscasDiscDetailParserGenreTests
{
    /// <summary>グローバルナビの「すべてのジャンル」を誤認せず、詳細情報行だけから階層を取得することを確認する</summary>
    [Fact]
    public void Parse_GenreRow_ExtractsOnlyProductGenrePath()
    {
        const string html = """
            <html><body>
              <nav>すべてのジャンル J-POP ワールド アニメ／ゲーム</nav>
              <h1>作品タイトル / アーティスト</h1>
              <div>レンタル開始日：2026年8月30日</div>
              <dl>
                <dt>ジャンル：</dt>
                <dd><a href="searchCd.do?G=01">J-POP</a> &gt; <a href="searchCd.do?G=0101">J-POP</a></dd>
              </dl>
              <div>曲目： 1. 曲名 (3分00秒) 記番： TEST</div>
            </body></html>
            """;

        var result = new DiscasDiscDetailParser().Parse(
            html,
            new Uri("https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=1"));

        Assert.Equal(["J-POP", "J-POP"], result.GenrePath);
    }
}
