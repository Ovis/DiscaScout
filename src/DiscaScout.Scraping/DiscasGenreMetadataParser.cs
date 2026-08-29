using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCAS検索結果HTMLに埋め込まれた商品ジャンル情報を解析する
/// </summary>
internal static partial class DiscasGenreMetadataParser
{
    /// <summary>
    /// ページ内のGA用商品メタデータからtitleIDとジャンルの対応表を生成する
    /// </summary>
    /// <param name="document">解析済みの検索結果HTML</param>
    /// <returns>titleIDをキーとしたジャンル情報</returns>
    internal static IReadOnlyDictionary<string, DiscasGenreMetadata> Parse(IDocument document)
    {
        var result = new Dictionary<string, DiscasGenreMetadata>(StringComparer.Ordinal);

        foreach (var script in document.QuerySelectorAll("script"))
        {
            foreach (Match match in GenreMetadataRegex().Matches(script.TextContent))
            {
                var large = Normalize(match.Groups["large"].Value);
                if (large is null)
                {
                    continue;
                }

                result[match.Groups["id"].Value] = new DiscasGenreMetadata(
                    large,
                    Normalize(match.Groups["middle"].Value),
                    Normalize(match.Groups["small"].Value));
            }
        }

        return result;
    }

    private static string? Normalize(string value)
    {
        var normalized = WhitespaceRegex().Replace(value, " ").Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? null
            : normalized;
    }

    // 実ページでは各商品に対するGA用JavaScriptオブジェクトへ、大・中・小ジャンルとtitleIDが
    // この順序で埋め込まれている。商品詳細ページを追加取得せずジャンルを得るため、この既存データを利用する。
    [GeneratedRegex(@"genre_large\s*:\s*'(?<large>[^']*)'\s*,\s*genre_mid\s*:\s*'(?<middle>[^']*)'\s*,\s*genre_min\s*:\s*'(?<small>[^']*)'\s*,\s*titleid\s*:\s*'(?<id>[^']+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenreMetadataRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// DISCASの商品ジャンル階層を保持する
/// </summary>
/// <param name="Large">大ジャンル</param>
/// <param name="Middle">中ジャンル。値がない場合はnull</param>
/// <param name="Small">小ジャンル。値がない場合はnull</param>
internal sealed record DiscasGenreMetadata(string Large, string? Middle, string? Small);
