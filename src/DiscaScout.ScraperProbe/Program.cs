using DiscaScout.Scraping;

const string defaultUrl = "https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?G=01013&PA=g_sk_&PN=1&SK=discas_music_new";

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

foreach (var productLink in analysis.ProductLinks.Take(10))
{
    Console.WriteLine($"Product: {productLink}");
}

return result.StatusCode is >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices ? 0 : 1;
