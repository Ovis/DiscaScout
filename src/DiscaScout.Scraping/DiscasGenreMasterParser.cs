using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>DISCASの「すべてのジャンル」ページからジャンル階層と外部IDを抽出する</summary>
public sealed class DiscasGenreMasterParser
{
    private readonly HtmlParser parser = new();

    /// <summary>ジャンルマスターページを解析する</summary>
    /// <param name="html">DISCASから取得してデコード済みの「すべてのジャンル」HTML</param>
    /// <returns>外部ID、表示名、親外部ID、兄弟内表示順を持つジャンル一覧</returns>
    public IReadOnlyList<ScrapedGenre> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = parser.ParseDocument(html);
        var result = new List<ScrapedGenre>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextSortOrder = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var externalId = GetGenreId(anchor.GetAttribute("href"));
            var name = Normalize(anchor.TextContent);
            if (externalId is null || name is null || name == "すべてのジャンル" || !seen.Add(externalId)) continue;

            var parentExternalId = FindParentGenreId(anchor);
            // SortOrderは兄弟間だけで意味を持つ。ページ全体の通番を保存すると親が異なるだけで
            // 表示順が不自然になるため、親外部IDごとに0始まりで採番する。
            var parentKey = parentExternalId ?? string.Empty;
            nextSortOrder.TryGetValue(parentKey, out var sortOrder);
            nextSortOrder[parentKey] = sortOrder + 1;
            result.Add(new ScrapedGenre(externalId, name, parentExternalId, sortOrder));
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
/// <param name="ExternalId">DISCASのジャンルリンクに含まれるGパラメータ</param>
/// <param name="Name">表示名</param>
/// <param name="ParentExternalId">親ジャンルのGパラメータ。ルートの場合はnull</param>
/// <param name="SortOrder">同一親配下での表示順</param>
public sealed record ScrapedGenre(string ExternalId, string Name, string? ParentExternalId, int SortOrder);
