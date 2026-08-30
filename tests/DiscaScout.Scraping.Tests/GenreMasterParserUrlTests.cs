using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// ジャンルリンクの外部ID抽出境界を検証する
/// </summary>
public sealed class GenreMasterParserUrlTests
{
    /// <summary>複数ジャンルをまとめたG値は単一マスターノードとして扱わないことを確認する</summary>
    [Fact]
    public void Parse_CombinedGenreParameter_IsIgnored()
    {
        const string html = """
            <ul>
              <li><a href="searchCd.do?G=01">J-POP</a></li>
              <li><a href="searchCd.do?G=01,02">まとめ</a></li>
            </ul>
            """;

        var result = new DiscasGenreMasterParser().Parse(html);

        var genre = Assert.Single(result);
        Assert.Equal("01", genre.ExternalId);
    }
}
