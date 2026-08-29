using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

public sealed class HtmlProbeAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_FindsProductLinksAndImages()
    {
        const string html = """
            <html>
              <head><title>Sample</title></head>
              <body>
                <a href="/netdvd/cd/goodsDetail.do?titleID=123456">CD</a>
                <img src="/images/jacket.jpg">
              </body>
            </html>
            """;

        var analyzer = new HtmlProbeAnalyzer();
        var result = await analyzer.AnalyzeAsync(html, new Uri("https://example.test/search"));

        Assert.Equal("Sample", result.Title);
        Assert.Single(result.ProductLinks);
        Assert.Equal("https://example.test/netdvd/cd/goodsDetail.do?titleID=123456", result.ProductLinks[0].ToString());
        Assert.Single(result.ImageUris);
        Assert.Equal("https://example.test/images/jacket.jpg", result.ImageUris[0].ToString());
    }
}
