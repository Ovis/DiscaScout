using DiscaScout.Scraping;

const string defaultUrl = "https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&PN=1&SK=discas_music_new&SRT=5";

var url = args.Length > 0 ? args[0] : defaultUrl;
if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
{
    Console.Error.WriteLine($"Invalid URL: {url}");
    return 2;
}

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All
};
using var httpClient = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(30)
};

var fetcher = new DiscasPageFetcher(httpClient);
var result = await fetcher.FetchAsync(uri);

Console.WriteLine($"Status: {(int)result.StatusCode} {result.StatusCode}");
Console.WriteLine($"Final URI: {result.FinalUri}");
Console.WriteLine($"Charset: {result.Charset ?? "(not specified)"}");
Console.WriteLine($"HTML length: {result.Html.Length:N0}");

var outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "probe");
Directory.CreateDirectory(outputDirectory);
var outputPath = Path.Combine(outputDirectory, "search-result.html");
await File.WriteAllTextAsync(outputPath, result.Html);
Console.WriteLine($"Saved HTML: {outputPath}");

var analyzer = new HtmlProbeAnalyzer();
var analysis = await analyzer.AnalyzeAsync(result.Html, result.FinalUri);

Console.WriteLine($"Document title: {analysis.Title ?? "(none)"}");
Console.WriteLine($"Anchors: {analysis.AnchorCount:N0}");
Console.WriteLine($"Unique links: {analysis.UniqueLinkCount:N0}");
Console.WriteLine($"Candidate product links: {analysis.ProductLinks.Count:N0}");
Console.WriteLine($"Images: {analysis.ImageUris.Count:N0}");

var parser = new DiscasSearchResultParser();
var searchPage = parser.Parse(result.Html, result.FinalUri, DiscSourceCategory.New);

Console.WriteLine($"Parsed products: {searchPage.Products.Count:N0}");
Console.WriteLine($"Reported total: {(searchPage.TotalCount is null ? "(unknown)" : searchPage.TotalCount.Value.ToString("N0"))}");
Console.WriteLine($"Hidden title IDs: {searchPage.HiddenTitleIds.Count:N0}");

var parsedIds = searchPage.Products.Select(product => product.DiscasId).ToHashSet(StringComparer.Ordinal);
var hiddenIds = searchPage.HiddenTitleIds.ToHashSet(StringComparer.Ordinal);
var idsMatch = hiddenIds.Count == 0 || parsedIds.SetEquals(hiddenIds);
Console.WriteLine($"Product IDs match hidden IDs: {idsMatch}");

foreach (var product in searchPage.Products.Take(10))
{
    Console.WriteLine($"#{product.SourceRank} [{product.DiscasId}] {product.Title} / {product.Artist}");
    Console.WriteLine($"  URL: {product.ProductUrl}");
    Console.WriteLine($"  Image: {product.ImageUrl ?? "(none)"}");
}

if (result.StatusCode is < System.Net.HttpStatusCode.OK or >= System.Net.HttpStatusCode.MultipleChoices)
{
    return 1;
}

// hidden titleIdは実ページ内で同じ40商品を列挙しているため、ここが不一致ならDOM変更や解析漏れを疑う。
// 本番クロールでも部分データを正常扱いしないための検証材料として利用する。
return searchPage.Products.Count > 0 && idsMatch ? 0 : 3;
