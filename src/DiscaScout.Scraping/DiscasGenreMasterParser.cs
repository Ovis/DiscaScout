using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>DISCASの「すべてのジャンル」ページからジャンル階層と外部IDを抽出する</summary>
public sealed class DiscasGenreMasterParser
{
    private readonly HtmlParser parser = new();

    /// <summary>ジャンルマスターページを解析する</summary>
    public IReadOnlyList<ScrapedGenre> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = parser.ParseDocument(html);
        var result = new List<ScrapedGenre>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var externalId = GetGenreId(anchor.GetAttribute("href"));
            var name = Normalize(anchor.TextContent);
            if (externalId is null || name is null || name == "すべてのジャンル" || !seen.Add(externalId)) continue;
            result.Add(new ScrapedGenre(externalId, name, FindParentGenreId(anchor), result.Count));
        }
        return result;
    }

    private static string? FindParentGenreId(IElement anchor)
    {
        var parentLi = anchor.Closest("li")?.ParentElement?.Closest("li");
        if (parentLi is null) return null;
        foreach (var child in parentLi.Children)
        {
            if (child.LocalName == "a" && GetGenreId(child.GetAttribute("href")) is { } id) return id;
        }
        return null;
    }

    private static string? GetGenreId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var queryIndex = href.IndexOf('?');
        if (queryIndex < 0) return null;
        foreach (var part in href[(queryIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var key = Uri.UnescapeDataString(part[..separator]);
            if (!key.Equals("G", StringComparison.OrdinalIgnoreCase)) continue;
            var value = Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' ')).Trim();
            return value.Length == 0 || value.Contains(',') ? null : value;
        }
        return null;
    }

    private static string? Normalize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>DISCASジャンルマスターページから取得した1ノードを保持する</summary>
public sealed record ScrapedGenre(string ExternalId, string Name, string? ParentExternalId, int SortOrder);
