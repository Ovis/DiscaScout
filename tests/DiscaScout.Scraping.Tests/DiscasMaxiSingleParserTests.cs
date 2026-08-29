using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCAS検索結果タイトルからのマキシシングル判定を検証する
/// </summary>
public sealed class DiscasMaxiSingleParserTests
{
    private static readonly Uri PageUri = new(
        "https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?PN=1&SK=discas_music_new&SRT=5");

    /// <summary>
    /// タイトル先頭が【MAXI】の商品だけをマキシシングルとして判定する
    /// </summary>
    [Theory]
    [InlineData("【MAXI】テストシングル", true)]
    [InlineData("テスト【MAXI】シングル", false)]
    [InlineData("通常アルバム", false)]
    public void Parse_TitlePrefix_DetectsMaxiSingle(string title, bool expected)
    {
        var html = $$"""
            <div class="cd-product-item">
              <div class="card-body-searchCd">
                <h3 class="cd-search-product-title">
                  <a class="card-title-searchCd" href="goodsDetail.do?titleID=123">{{title}}</a>
                </h3>
                <h3 class="cd-search-product-title">Artist</h3>
              </div>
            </div>
            <script>
              var itemTopGATag123 = {event:'teigaku_search_cd', category:'CD', genre_large:'J-POP', genre_mid:'J-POP', genre_min:'null', titleid:'123'};
            </script>
            """;
        var parser = new DiscasSearchResultParser();

        var result = parser.Parse(html, PageUri, DiscSourceCategory.New);

        Assert.Equal(expected, Assert.Single(result.Products).IsMaxiSingle);
    }
}
