using System.Web;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASの「すべてのジャンル」ページからジャンル階層と外部IDを抽出する
/// </summary>
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

            // genreAll.doは階層を入れ子のリストとして表現している。親li直下のジャンルリンクを
            // 親ノードとして採用し、表示名が同じノードでも外部IDで区別する。
            var parentExternalId = FindParentGenreId(anchor);
            var siblingAnchors = anchor.ParentElement?.ParentElement?.Children
                .SelectMany(x => x.QuerySelectorAll(":scope > a[href]"))
                .Where(x => GetGenreId(x.GetAttribute("href")) is not null)
                .ToArray() ?? [];
            var sortOrder = Array.IndexOf(siblingAnchors, anchor);
            result.Add(new ScrapedGenre(externalId, name, parentExternalId, sortOrder < 0 ? result.Count : sortOrder));
        }

        return result;
    }

    private static string? FindParentGenreId(IElement anchor)
    {
        var li = anchor.Closest("li");
        var parentLi = li?.ParentElement?.Closest("li");
        if (parentLi is null) return null;
        foreach (var child in parentLi.Children)
        {
            if (child.LocalName != "a") continue;
            var id = GetGenreId(child.GetAttribute("href"));
            if (id is not null) return id;
        }
        return null;
    }

    private static string? GetGenreId(string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        var queryIndex = href.IndexOf('?');
        if (queryIndex < 0) return null;
        var query = HttpUtility.ParseQueryString(href[(queryIndex + 1)..]);
        var value = query["G"] ?? query["g"];
        if (string.IsNullOrWhiteSpace(value) || value.Contains(',')) return null;
        return value.Trim();
    }

    private static string? Normalize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>DISCASジャンルマスターページから取得した1ノードを保持する</summary>
public sealed record ScrapedGenre(string ExternalId, string Name, string? ParentExternalId, int SortOrder);
