using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCASの「すべてのジャンル」HTMLから階層と外部IDを抽出する処理を検証する
/// </summary>
public sealed class DiscasGenreMasterParserTests
{
    /// <summary>
    /// 実ページ同様に大ジャンル見出しと子ジャンル一覧が兄弟要素で並ぶHTMLから階層を復元できることを確認する
    /// </summary>
    [Fact]
    public void Parse_ActualLayout_ExtractsHierarchyFromCompositeGenreParameter()
    {
        const string html = """
            <html><body>
              <div id="mainContents">
                <div class="ppdis00033WrapB">
                  <div class="ppdis00033WrapC">
                    <h2><a href="https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?g=01013">アニメ／ゲーム</a></h2>
                  </div>
                  <div class="ppdis00033OuterA">
                    <ul class="ppdis00033ListA">
                      <li><a href="https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?g=01013,01070">アニメ／ゲーム</a></li>
                      <li><a href="https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?g=01013,01072">声優</a></li>
                    </ul>
                  </div>
                </div>
                <div class="ppdis00033WrapB">
                  <div class="ppdis00033WrapC">
                    <h2><a href="https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?g=01004">ヒップホップ／ラップ</a></h2>
                  </div>
                  <div class="ppdis00033OuterA">
                    <ul class="ppdis00033ListA">
                      <li><a href="https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?g=01004,01038">オムニバス</a></li>
                    </ul>
                  </div>
                </div>
              </div>
              <nav><a href="searchCd.do?g=99999">ヘッダー上の別リンク</a></nav>
            </body></html>
            """;

        var result = new DiscasGenreMasterParser().Parse(html);

        Assert.Collection(result,
            genre =>
            {
                Assert.Equal("01013", genre.ExternalId);
                Assert.Equal("アニメ／ゲーム", genre.Name);
                Assert.Null(genre.ParentExternalId);
                Assert.Equal(0, genre.SortOrder);
            },
            genre =>
            {
                Assert.Equal("01013,01070", genre.ExternalId);
                Assert.Equal("アニメ／ゲーム", genre.Name);
                Assert.Equal("01013", genre.ParentExternalId);
                Assert.Equal(0, genre.SortOrder);
            },
            genre =>
            {
                Assert.Equal("01013,01072", genre.ExternalId);
                Assert.Equal("声優", genre.Name);
                Assert.Equal("01013", genre.ParentExternalId);
                Assert.Equal(1, genre.SortOrder);
            },
            genre =>
            {
                Assert.Equal("01004", genre.ExternalId);
                Assert.Equal("ヒップホップ／ラップ", genre.Name);
                Assert.Null(genre.ParentExternalId);
                Assert.Equal(1, genre.SortOrder);
            },
            genre =>
            {
                Assert.Equal("01004,01038", genre.ExternalId);
                Assert.Equal("オムニバス", genre.Name);
                Assert.Equal("01004", genre.ParentExternalId);
                Assert.Equal(0, genre.SortOrder);
            });
    }
}
