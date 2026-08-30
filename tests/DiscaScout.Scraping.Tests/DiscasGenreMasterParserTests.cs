using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCASの「すべてのジャンル」HTMLから階層と外部IDを抽出する処理を検証する
/// </summary>
public sealed class DiscasGenreMasterParserTests
{
    /// <summary>同名ジャンルを含む3段階層をGパラメータで区別して解析できることを確認する</summary>
    [Fact]
    public void Parse_NestedList_ExtractsHierarchyAndSiblingOrder()
    {
        const string html = """
            <html><body>
              <ul>
                <li><a href="searchCd.do?G=01">J-POP</a>
                  <ul>
                    <li><a href="searchCd.do?G=0101">J-POP</a>
                      <ul><li><a href="searchCd.do?G=010101">国内ポップス</a></li></ul>
                    </li>
                    <li><a href="searchCd.do?G=0102">ロック</a></li>
                  </ul>
                </li>
                <li><a href="searchCd.do?G=02">アニメ／ゲーム</a></li>
              </ul>
            </body></html>
            """;

        var result = new DiscasGenreMasterParser().Parse(html);

        Assert.Collection(result,
            genre => { Assert.Equal("01", genre.ExternalId); Assert.Null(genre.ParentExternalId); Assert.Equal(0, genre.SortOrder); },
            genre => { Assert.Equal("0101", genre.ExternalId); Assert.Equal("01", genre.ParentExternalId); Assert.Equal(0, genre.SortOrder); },
            genre => { Assert.Equal("010101", genre.ExternalId); Assert.Equal("0101", genre.ParentExternalId); Assert.Equal(0, genre.SortOrder); },
            genre => { Assert.Equal("0102", genre.ExternalId); Assert.Equal("01", genre.ParentExternalId); Assert.Equal(1, genre.SortOrder); },
            genre => { Assert.Equal("02", genre.ExternalId); Assert.Null(genre.ParentExternalId); Assert.Equal(1, genre.SortOrder); });
    }
}
