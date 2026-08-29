using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCAS検索結果HTMLの解析を検証する
/// </summary>
public sealed class DiscasSearchResultParserTests
{
    private static readonly Uri PageUri = new(
        "https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&PN=1&SK=discas_music_new&SRT=5");

    /// <summary>
    /// 実ページから縮約したfixtureを商品単位で解析できることを確認する
    /// </summary>
    [Fact]
    public async Task Parse_SearchResultFixture_ExtractsProductsAndPageMetadata()
    {
        var html = await File.ReadAllTextAsync(GetFixturePath("search-result-sample.html"));
        var parser = new DiscasSearchResultParser();

        var result = parser.Parse(html, PageUri, DiscSourceCategory.New, sourceRankOffset: 40);

        Assert.Equal(1162, result.TotalCount);
        Assert.Equal(["7720224056", "7635390762"], result.HiddenTitleIds);
        Assert.Equal(2, result.Products.Count);

        var first = result.Products[0];
        Assert.Equal("7720224056", first.DiscasId);
        Assert.Equal(
            "https://movie-tsutaya.tsite.jp/netdvd/cd/goodsDetail.do?titleID=7720224056",
            first.ProductUrl);
        Assert.Equal("機動警察パトレイバー Early Days MUSIC COLLECTION CD BOX【Disc.5&Disc.6】", first.Title);
        Assert.Equal("機動警察パトレイバー", first.Artist);
        Assert.Equal(
            "https://img.discas.net/img/jacket/core/202607/16/4540774391875_1SX.jpg",
            first.ImageUrl);
        Assert.Null(first.RentalStartDate);
        Assert.Equal(DiscSourceCategory.New, first.Category);
        Assert.Equal(41, first.SourceRank);

        var second = result.Products[1];
        Assert.Equal("7635390762", second.DiscasId);
        Assert.Equal("#アニソンジャズ FIRST", second.Title);
        Assert.Equal("#アニソンジャズ", second.Artist);
        Assert.Null(second.ImageUrl);
        Assert.Equal(42, second.SourceRank);
    }

    /// <summary>
    /// mobile向けDOMが同じHTMLに存在しても商品を重複解析しないことを確認する
    /// </summary>
    [Fact]
    public async Task Parse_SearchResultFixture_IgnoresMobileProductMarkup()
    {
        var html = await File.ReadAllTextAsync(GetFixturePath("search-result-sample.html"));
        var parser = new DiscasSearchResultParser();

        var result = parser.Parse(html, PageUri, DiscSourceCategory.New);

        Assert.Equal(2, result.Products.Count);
        Assert.Equal(2, result.Products.Select(product => product.DiscasId).Distinct().Count());
    }

    /// <summary>
    /// 必須のアーティストDOMが欠落した商品を正常データとして扱わないことを確認する
    /// </summary>
    [Fact]
    public void Parse_ProductWithoutArtist_ThrowsParseException()
    {
        const string html = """
            <div class="cd-product-item">
              <a class="card-title-searchCd" href="goodsDetail.do?titleID=123">Title</a>
              <img class="card-img" src="https://img.discas.net/example.jpg">
            </div>
            """;
        var parser = new DiscasSearchResultParser();

        var exception = Assert.Throws<DiscasSearchParseException>(
            () => parser.Parse(html, PageUri, DiscSourceCategory.New));

        Assert.Contains("アーティストリンク", exception.Message);
    }

    /// <summary>
    /// テスト出力へコピーしたfixtureの絶対パスを取得する
    /// </summary>
    private static string GetFixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
