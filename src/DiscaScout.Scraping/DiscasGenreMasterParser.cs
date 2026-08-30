using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASの「すべてのジャンル」ページからジャンル階層と外部IDを抽出する
/// </summary>
public sealed class DiscasGenreMasterParser
{
    private readonly HtmlParser parser = new();

    /// <summary>
    /// ジャンルマスターページを解析する
    /// </summary>
    /// <param name="html">DISCASから取得してデコード済みの「すべてのジャンル」HTML</param>
    /// <returns>外部ID、表示名、親外部ID、兄弟内表示順を持つジャンル一覧</returns>
    public IReadOnlyList<ScrapedGenre> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var document = parser.ParseDocument(html);
        var result = new List<ScrapedGenre>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nextSortOrder = new Dictionary<string, int>(StringComparer.Ordinal);

        // 実際のgenreAll.doはul/liのネストで階層を表していない。
        // 大ジャンルはppdis00033WrapB内のh2、配下ジャンルは同じブロック内の一覧として並び、
        // Gパラメータ自体が「01013,01072」のように親から子までの経路を保持している。
        foreach (var anchor in document.QuerySelectorAll(".ppdis00033WrapB a[href]"))
        {
            var externalId = GetGenrePathId(anchor.GetAttribute("href"));
            var name = Normalize(anchor.TextContent);
            if (externalId is null || name is null || !seen.Add(externalId)) continue;

            var parentExternalId = GetParentPathId(externalId);
            var parentKey = parentExternalId ?? string.Empty;
            nextSortOrder.TryGetValue(parentKey, out var sortOrder);
            nextSortOrder[parentKey] = sortOrder + 1;
            result.Add(new ScrapedGenre(externalId, name, parentExternalId, sortOrder));
        }

        return result;
    }

    private static string? GetGenrePathId(string? href)
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
            if (value.Length == 0) return null;

            var segments = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || segments.Any(x => x.Length == 0)) return null;
            return string.Join(',', segments);
        }

        return null;
    }

    private static string? GetParentPathId(string externalId)
    {
        var separator = externalId.LastIndexOf(',');
        return separator < 0 ? null : externalId[..separator];
    }

    private static string? Normalize(string value)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? null : normalized;
    }
}

/// <summary>
/// DISCASジャンルマスターページから取得した1ノードを保持する
/// </summary>
/// <param name="ExternalId">DISCASのGパラメータが表すルートから当該ノードまでのジャンル経路</param>
/// <param name="Name">表示名</param>
/// <param name="ParentExternalId">親ノードまでのGパラメータ経路。ルートの場合はnull</param>
/// <param name="SortOrder">同一親配下での表示順</param>
public sealed record ScrapedGenre(string ExternalId, string Name, string? ParentExternalId, int SortOrder);
