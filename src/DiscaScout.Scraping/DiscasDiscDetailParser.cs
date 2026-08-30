using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>DISCASのCD詳細HTMLから、商品名・アーティストと補完メタデータを抽出する</summary>
public sealed partial class DiscasDiscDetailParser
{
    private const string TwoDiscImageFileName = "tx_item_info03.png";
    private readonly HtmlParser htmlParser = new();

    /// <summary>CD詳細ページを解析する</summary>
    public DiscasDiscDetail Parse(string html, Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html); ArgumentNullException.ThrowIfNull(pageUri);
        var document = htmlParser.ParseDocument(html);
        var normalizedText = NormalizeText(document.Body?.TextContent ?? document.DocumentElement.TextContent);
        var rentalStartDate = ParseRentalStartDate(normalizedText) ?? throw new DiscasDiscDetailParseException($"レンタル開始日を取得できない: {pageUri}");
        var (title, artist) = ParseTitleAndArtist(document.QuerySelector("h1")?.TextContent, pageUri);
        var isTwoDisc = document.Images.Any(image => image.GetAttribute("src")?.Contains(TwoDiscImageFileName, StringComparison.OrdinalIgnoreCase) == true);
        return new DiscasDiscDetail(title, artist, rentalStartDate, ExtractDescription(normalizedText), ExtractGenrePath(document), isTwoDisc, ExtractTracks(normalizedText), ExtractDetailImageUrl(document, pageUri));
    }

    private static IReadOnlyList<string> ExtractGenrePath(IDocument document)
    {
        // ページ全体の文字列から「ジャンル」を検索するとグローバルナビの「すべてのジャンル」を誤認するため、
        // 「ジャンル：」ラベルを含む情報行だけを対象にし、その行内のリンク表示名から階層を復元する。
        foreach (var element in document.QuerySelectorAll("th,dt,td,div,li,p"))
        {
            var ownText = NormalizeText(string.Concat(element.ChildNodes.Where(x => x is not IElement).Select(x => x.TextContent)));
            if (!GenreLabelRegex().IsMatch(ownText)) continue;
            var container = element.LocalName is "th" or "dt" ? element.NextElementSibling : element;
            if (container is null) continue;
            var links = container.QuerySelectorAll("a").Select(x => NormalizeText(x.TextContent)).Where(x => x.Length > 0).ToArray();
            if (links.Length > 0) return links;
            var text = GenreLabelRegex().Replace(NormalizeText(container.TextContent), string.Empty).Trim();
            if (text.Length > 0) return text.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        return [];
    }

    private static string? ExtractDetailImageUrl(IDocument document, Uri pageUri)
    {
        var source = document.QuerySelector(".itemJacketWrapOuter #jacketImgM")?.GetAttribute("src") ?? document.QuerySelector(".itemJacketWrapOuter img")?.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(source)) return null;
        return Uri.TryCreate(pageUri, source.Trim(), out var imageUri) && imageUri.Scheme is "http" or "https" ? imageUri.AbsoluteUri : null;
    }

    private static (string Title, string Artist) ParseTitleAndArtist(string? headingText, Uri pageUri)
    {
        var heading = NormalizeText(headingText ?? string.Empty); var separatorIndex = heading.LastIndexOf(" / ", StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex + 3 >= heading.Length) throw new DiscasDiscDetailParseException($"商品名とアーティストを取得できない: {pageUri}");
        var title = heading[..separatorIndex].Trim(); var artist = heading[(separatorIndex + 3)..].Trim();
        if (title.Length == 0 || artist.Length == 0) throw new DiscasDiscDetailParseException($"商品名とアーティストを取得できない: {pageUri}");
        return (title, artist);
    }

    private static DateOnly? ParseRentalStartDate(string text)
    {
        var match = RentalStartDateRegex().Match(text); return match.Success ? new DateOnly(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) : null;
    }

    private static string? ExtractDescription(string text)
    {
        var match = DescriptionRegex().Match(text); if (!match.Success) return null; var description = match.Groups[1].Value.Trim(); return description.Length == 0 ? null : description;
    }

    private static IReadOnlyList<ScrapedDiscTrack> ExtractTracks(string text)
    {
        var sectionMatch = TrackSectionRegex().Match(text); if (!sectionMatch.Success) return [];
        var tracks = new List<ScrapedDiscTrack>();
        foreach (Match match in TrackRegex().Matches(sectionMatch.Groups[1].Value))
        {
            var title = match.Groups[2].Value.Trim(); if (title.Length == 0) continue;
            tracks.Add(new ScrapedDiscTrack(int.Parse(match.Groups[1].Value), title, match.Groups[3].Success ? match.Groups[3].Value.Trim() : null));
        }
        return tracks.GroupBy(x => x.TrackNumber).Select(x => x.First()).OrderBy(x => x.TrackNumber).ToArray();
    }

    private static string NormalizeText(string text) => WhitespaceRegex().Replace(text, " ").Trim();
    [GeneratedRegex(@"レンタル開始日\s*[：:]?\s*(\d{4})年\s*(\d{1,2})月\s*(\d{1,2})日", RegexOptions.CultureInvariant)] private static partial Regex RentalStartDateRegex();
    [GeneratedRegex(@"作品詳細\s*(.*?)\s*ジャンル(?:\s|[：:])", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex DescriptionRegex();
    [GeneratedRegex(@"^\s*ジャンル\s*[：:]", RegexOptions.CultureInvariant)] private static partial Regex GenreLabelRegex();
    [GeneratedRegex(@"曲目\s*[：:]?\s*(.*?)(?=\s*記番(?:\s|[：:]))", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex TrackSectionRegex();
    [GeneratedRegex(@"(?:^|\s)(\d+)\.\s*(.*?)(?:\s*\((\d+分\d+秒)\))(?=\s+\d+\.|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)] private static partial Regex TrackRegex();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex WhitespaceRegex();
}

/// <summary>DISCAS詳細ページから取得したCD情報を保持する</summary>
public sealed record DiscasDiscDetail(string Title, string Artist, DateOnly RentalStartDate, string? Description, IReadOnlyList<string> GenrePath, bool IsTwoDisc, IReadOnlyList<ScrapedDiscTrack> Tracks, string? DetailImageUrl);
/// <summary>DISCAS詳細ページから取得した1曲分の情報を保持する</summary>
public sealed record ScrapedDiscTrack(int TrackNumber, string Title, string? Duration);
/// <summary>DISCAS詳細ページを安全に解析できない場合に発生する例外</summary>
public sealed class DiscasDiscDetailParseException : Exception { public DiscasDiscDetailParseException(string message) : base(message) { } }
