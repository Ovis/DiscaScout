using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// ジャンルリンクの外部ID抽出境界を検証する
/// </summary>
public sealed class GenreMasterParserUrlTests
{
    /// <summary>
    /// 実ページで階層を表す複合G値を完全パスとして保持し、対象領域外のリンクは取り込まないことを確認する
    /// </summary>
    [Fact]
    public void Parse_CompositeGenreParameter_IsPreservedAsPath()
    {
        const string html = """
            <html><body>
              <div class="ppdis00033WrapB">
                <h2><a href="searchCd.do?G=01013">アニメ／ゲーム</a></h2>
                <ul class="ppdis00033ListA">
                  <li><a href="searchCd.do?G=01013,01072">声優</a></li>
                </ul>
              </div>
              <nav><a href="searchCd.do?G=99999">対象外リンク</a></nav>
            </body></html>
            """;

        var result = new DiscasGenreMasterParser().Parse(html);

        Assert.Collection(result,
            root =>
            {
                Assert.Equal("01013", root.ExternalId);
                Assert.Null(root.ParentExternalId);
            },
            child =>
            {
                Assert.Equal("01013,01072", child.ExternalId);
                Assert.Equal("01013", child.ParentExternalId);
                Assert.Equal("声優", child.Name);
            });
    }
}
