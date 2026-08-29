using DiscaScout.Scraping;

var crawlMode = args.Length > 0 && args[0].Equals("--crawl", StringComparison.OrdinalIgnoreCase);
var category = args.Length > 1 && args[1].Equals("upcoming", StringComparison.OrdinalIgnoreCase)
    ? DiscSourceCategory.Upcoming
    : DiscSourceCategory.New;

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = true,
    AutomaticDecompression = System.Net.DecompressionMethods.All
};
using var httpClient = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(30)
};

var fetcher = new DiscasPageFetcher(httpClient, new DiscasRequestThrottle());
var parser = new DiscasSearchResultParser();
var outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "probe");
Directory.CreateDirectory(outputDirectory);

if (crawlMode)
{
    var crawler = new DiscasCategoryCrawler(fetcher, parser);
    var snapshot = await crawler.CrawlAsync(category, SaveFetchedPageAsync);

    Console.WriteLine($"Category: {snapshot.Category}");
    Console.WriteLine($"Reported total: {snapshot.TotalCount:N0}");
    Console.WriteLine($"Pages: {snapshot.PageCount:N0}");
    Console.WriteLine($"Parsed products: {snapshot.Products.Count:N0}");

    foreach (var genre in snapshot.Products.GroupBy(x => x.GenreLarge).OrderByDescending(x => x.Count()))
    {
        Console.WriteLine($"Genre: {genre.Key} = {genre.Count():N0}");
    }

    foreach (var product in snapshot.Products.Take(10))
    {
        Console.WriteLine($"#{product.SourceRank} [{product.DiscasId}] {product.Title} / {product.Artist} / {product.GenreLarge}");
    }

    return 0;
}

var uri = DiscasSearchTarget.CreateUri(category, 1);
var result = await fetcher.FetchAsync(uri);

Console.WriteLine($"Status: {(int)result.StatusCode} {result.StatusCode}");
Console.WriteLine($"Final URI: {result.FinalUri}");
Console.WriteLine($"Charset: {result.Charset ?? "(not specified)"}");
Console.WriteLine($"HTML length: {result.Html.Length:N0}");

var outputPath = Path.Combine(outputDirectory, "search-result.html");
await File.WriteAllTextAsync(outputPath, result.Html);
Console.WriteLine($"Saved HTML: {outputPath}");

var analyzer = new HtmlProbeAnalyzer();
var analysis = await analyzer.AnalyzeAsync(result.Html, result.FinalUri);
var page = parser.Parse(result.Html, result.FinalUri, category);
var parsedIds = page.Products.Select(x => x.DiscasId).ToArray();
var hiddenIdsMatch = parsedIds.SequenceEqual(page.HiddenTitleIds, StringComparer.Ordinal);

Console.WriteLine($"Document title: {analysis.Title ?? "(none)"}");
Console.WriteLine($"Anchors: {analysis.AnchorCount:N0}");
Console.WriteLine($"Unique links: {analysis.UniqueLinkCount:N0}");
Console.WriteLine($"Candidate product links: {analysis.ProductLinks.Count:N0}");
Console.WriteLine($"Images: {analysis.ImageUris.Count:N0}");
Console.WriteLine($"Parsed products: {page.Products.Count:N0}");
Console.WriteLine($"Reported total: {page.TotalCount?.ToString("N0") ?? "(unknown)"}");
Console.WriteLine($"Hidden title IDs: {page.HiddenTitleIds.Count:N0}");
Console.WriteLine($"Product IDs match hidden IDs: {hiddenIdsMatch}");

foreach (var product in page.Products.Take(10))
{
    Console.WriteLine($"#{product.SourceRank} [{product.DiscasId}] {product.Title} / {product.Artist}");
    Console.WriteLine($"  Genre: {product.GenreLarge} / {product.GenreMiddle ?? "(none)"} / {product.GenreSmall ?? "(none)"}");
    Console.WriteLine($"  URL: {product.ProductUrl}");
    Console.WriteLine($"  Image: {product.ImageUrl ?? "(none)"}");
}

if (result.StatusCode is < System.Net.HttpStatusCode.OK or >= System.Net.HttpStatusCode.MultipleChoices)
{
    return 1;
}

if (page.Products.Count == 0 || !hiddenIdsMatch)
{
    return 3;
}

return 0;

async ValueTask SaveFetchedPageAsync(DiscasFetchedPage fetchedPage, CancellationToken cancellationToken)
{
    var categoryName = fetchedPage.Category == DiscSourceCategory.New ? "new" : "upcoming";
    var fileName = $"{categoryName}-page-{fetchedPage.PageNumber:D3}.html";
    var path = Path.Combine(outputDirectory, fileName);

    // 解析より先にHTMLを保存することで、DOM変更や未知の商品形式でParseが失敗しても
    // 実際に返されたページを後からそのまま調査できるようにする。
    await File.WriteAllTextAsync(path, fetchedPage.Html, cancellationToken);
    Console.WriteLine($"Fetched page {fetchedPage.PageNumber}: {fetchedPage.Uri}");
    Console.WriteLine($"Saved HTML: {path}");
}
