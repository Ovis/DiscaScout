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
        Assert.Equal("アニメ／ゲーム", first.GenreLarge);
        Assert.Equal("アニメ", first.GenreMiddle);
        Assert.Null(first.GenreSmall);
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
        Assert.Equal("アニメ／ゲーム", second.GenreLarge);
        Assert.Equal("アニメ", second.GenreMiddle);
        Assert.Equal("ジャズ", second.GenreSmall);
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
    /// アーティスト名にリンクがなくても表示見出しから取得できることを確認する
    /// </summary>
    [Fact]
    public void Parse_ArtistWithoutLink_UsesSecondProductTitleHeading()
    {
        const string html = """
            <div class="cd-product-item">
              <div class="card-body-searchCd">
                <h3 class="cd-search-product-title">
                  <a class="card-title-searchCd" href="goodsDetail.do?titleID=123">Title</a>
                </h3>
                <h3 class="cd-search-product-title">Various Artists</h3>
              </div>
              <img class="card-img" src="https://img.discas.net/example.jpg">
            </div>
            <script>
              var itemTopGATag123 = {event:'teigaku_search_cd', category:'CD', genre_large:'J-POP', genre_mid:'J-POP', genre_min:'null', titleid:'123'};
            </script>
            """;
        var parser = new DiscasSearchResultParser();

        var result = parser.Parse(html, PageUri, DiscSourceCategory.New);

        Assert.Single(result.Products);
        Assert.Equal("Various Artists", result.Products[0].Artist);
    }

    /// <summary>
    /// 通常のアーティスト見出しがない商品でも専用見出しから表示名を取得できることを確認する
    /// </summary>
    [Fact]
    public void Parse_UnavailableArtistMarkup_UsesFallbackHeading()
    {
        const string html = """
            <div class="cd-product-item">
              <div class="card-body-searchCd">
                <h3 class="cd-search-product-title">
                  <a class="card-title-searchCd" href="goodsDetail.do?titleID=123">Title</a>
                </h3>
                <h3 class="cd-search-artist-not-available">トレンドジ―</h3>
              </div>
              <img class="card-img" src="https://img.discas.net/example.jpg">
            </div>
            <script>
              var itemTopGATag123 = {event:'teigaku_search_cd', category:'CD', genre_large:'J-POP', genre_mid:'J-POP', genre_min:'null', titleid:'123'};
            </script>
            """;
        var parser = new DiscasSearchResultParser();

        var result = parser.Parse(html, PageUri, DiscSourceCategory.New);

        Assert.Single(result.Products);
        Assert.Equal("トレンドジ―", result.Products[0].Artist);
    }

    /// <summary>
    /// ジャンル情報が欠落した商品を正常データとして扱わないことを確認する
    /// </summary>
    [Fact]
    public void Parse_ProductWithoutGenreMetadata_ThrowsParseException()
    {
        const string html = """
            <div class="cd-product-item">
              <div class="card-body-searchCd">
                <h3 class="cd-search-product-title">
                  <a class="card-title-searchCd" href="goodsDetail.do?titleID=123">Title</a>
                </h3>
                <h3 class="cd-search-product-title">Artist</h3>
              </div>
            </div>
            """;
        var parser = new DiscasSearchResultParser();

        var exception = Assert.Throws<DiscasSearchParseException>(
            () => parser.Parse(html, PageUri, DiscSourceCategory.New));

        Assert.Contains("ジャンル情報", exception.Message);
    }

    /// <summary>
    /// テスト出力へコピーしたfixtureの絶対パスを取得する
    /// </summary>
    private static string GetFixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }
}
