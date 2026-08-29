using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

public sealed class HtmlProbeAnalyzer
{
    public async Task<ProbeAnalysis> AnalyzeAsync(string html, Uri baseUri, CancellationToken cancellationToken = default)
    {
        var parser = new HtmlParser();
        var document = await parser.ParseDocumentAsync(html, cancellationToken);

        var links = document.QuerySelectorAll("a[href]")
            .Select(element => element.GetAttribute("href"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ResolveUri(baseUri, value!))
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .Distinct()
            .ToArray();

        var productLinks = links
            .Where(uri => uri.Query.Contains("titleID=", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.Contains("goodsDetail", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var images = document.QuerySelectorAll("img")
            .Select(element => element.GetAttribute("src"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ResolveUri(baseUri, value!))
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .Distinct()
            .ToArray();

        return new ProbeAnalysis(
            document.Title,
            document.QuerySelectorAll("a[href]").Length,
            links.Length,
            productLinks,
            images);
    }

    private static Uri? ResolveUri(Uri baseUri, string value) =>
        Uri.TryCreate(baseUri, value, out var uri) ? uri : null;
}

public sealed record ProbeAnalysis(
    string? Title,
    int AnchorCount,
    int UniqueLinkCount,
    IReadOnlyList<Uri> ProductLinks,
    IReadOnlyList<Uri> ImageUris);
