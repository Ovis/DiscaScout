using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASのCD詳細HTMLから、一覧では取得できない補完メタデータを抽出する
/// </summary>
public sealed partial class DiscasDiscDetailParser
{
    private const string TwoDiscImageFileName = "tx_item_info03.png";
    private readonly HtmlParser htmlParser = new();

    /// <summary>
    /// CD詳細ページを解析する
    /// </summary>
    /// <param name="html">DISCASから取得してデコード済みの詳細HTML</param>
    /// <param name="pageUri">取得した詳細ページURL</param>
    /// <returns>レンタル開始日、説明、2枚組判定、曲目を含む詳細情報</returns>
    /// <exception cref="DiscasDiscDetailParseException">レンタル開始日など完了判定に必要な情報を取得できない場合</exception>
    public DiscasDiscDetail Parse(string html, Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        var document = htmlParser.ParseDocument(html);
        var normalizedText = NormalizeText(document.Body?.TextContent ?? document.DocumentElement.TextContent);
        var rentalStartDate = ParseRentalStartDate(normalizedText)
            ?? throw new DiscasDiscDetailParseException($"レンタル開始日を取得できない: {pageUri}");

        var isTwoDisc = document.Images.Any(image =>
        {
            var source = image.GetAttribute("src");
            return !string.IsNullOrWhiteSpace(source)
                && source.Contains(TwoDiscImageFileName, StringComparison.OrdinalIgnoreCase);
        });

        return new DiscasDiscDetail(
            rentalStartDate,
            ExtractDescription(normalizedText),
            isTwoDisc,
            ExtractTracks(normalizedText));
    }

    private static DateOnly? ParseRentalStartDate(string text)
    {
        var match = RentalStartDateRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        return new DateOnly(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }

    private static string? ExtractDescription(string text)
    {
        var match = DescriptionRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var description = match.Groups[1].Value.Trim();
        return description.Length == 0 ? null : description;
    }

    private static IReadOnlyList<ScrapedDiscTrack> ExtractTracks(string text)
    {
        var sectionMatch = TrackSectionRegex().Match(text);
        if (!sectionMatch.Success)
        {
            return [];
        }

        var tracks = new List<ScrapedDiscTrack>();
        foreach (Match match in TrackRegex().Matches(sectionMatch.Groups[1].Value))
        {
            var trackNumber = int.Parse(match.Groups[1].Value);
            var title = match.Groups[2].Value.Trim();
            var duration = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
            if (title.Length == 0)
            {
                continue;
            }

            tracks.Add(new ScrapedDiscTrack(trackNumber, title, duration));
        }

        return tracks
            .GroupBy(x => x.TrackNumber)
            .Select(x => x.First())
            .OrderBy(x => x.TrackNumber)
            .ToArray();
    }

    private static string NormalizeText(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    [GeneratedRegex(@"レンタル開始日\s*[：:]?\s*(\d{4})年\s*(\d{1,2})月\s*(\d{1,2})日", RegexOptions.CultureInvariant)]
    private static partial Regex RentalStartDateRegex();

    [GeneratedRegex(@"作品詳細\s*(.*?)\s*ジャンル(?:\s|[：:])", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionRegex();

    // 詳細ページには曲目が複数箇所へ重複表示されることがあるため、最初の曲目ブロックから次の記番までだけを対象にする。
    [GeneratedRegex(@"曲目\s*[：:]?\s*(.*?)(?=\s*記番(?:\s|[：:]))", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TrackSectionRegex();

    [GeneratedRegex(@"(?:^|\s)(\d+)\.\s*(.*?)(?:\s*\((\d+分\d+秒)\))(?=\s+\d+\.|$)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TrackRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// DISCAS詳細ページから取得したCD補完情報を保持する
/// </summary>
/// <param name="RentalStartDate">レンタル開始日</param>
/// <param name="Description">作品詳細。ページに説明がない場合はnull</param>
/// <param name="IsTwoDisc">DISCASの2枚組アイコンが存在するかどうか</param>
/// <param name="Tracks">曲目一覧</param>
public sealed record DiscasDiscDetail(
    DateOnly RentalStartDate,
    string? Description,
    bool IsTwoDisc,
    IReadOnlyList<ScrapedDiscTrack> Tracks);

/// <summary>
/// DISCAS詳細ページから取得した1曲分の情報を保持する
/// </summary>
/// <param name="TrackNumber">曲順</param>
/// <param name="Title">曲名</param>
/// <param name="Duration">DISCAS上の演奏時間表記。取得できない場合はnull</param>
public sealed record ScrapedDiscTrack(int TrackNumber, string Title, string? Duration);

/// <summary>
/// DISCAS詳細ページを安全に解析できない場合に発生する例外
/// </summary>
public sealed class DiscasDiscDetailParseException : Exception
{
    /// <summary>
    /// 解析失敗理由を指定して例外を初期化する
    /// </summary>
    /// <param name="message">解析失敗の概要</param>
    public DiscasDiscDetailParseException(string message)
        : base(message)
    {
    }
}
