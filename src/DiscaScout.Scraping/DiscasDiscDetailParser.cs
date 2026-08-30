using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>
/// DISCASのCD詳細HTMLから、商品名・アーティストと補完メタデータを抽出する
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
    /// <returns>商品名、アーティスト、レンタル開始日、説明、2枚組判定、曲目、詳細用ジャケットURLを含む詳細情報</returns>
    /// <exception cref="DiscasDiscDetailParseException">商品名やレンタル開始日など必要な情報を取得できない場合</exception>
    public DiscasDiscDetail Parse(string html, Uri pageUri)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        var document = htmlParser.ParseDocument(html);
        var normalizedText = NormalizeText(document.Body?.TextContent ?? document.DocumentElement.TextContent);
        var rentalStartDate = ParseRentalStartDate(normalizedText)
            ?? throw new DiscasDiscDetailParseException($"レンタル開始日を取得できない: {pageUri}");
        var (title, artist) = ParseTitleAndArtist(document.QuerySelector("h1")?.TextContent, pageUri);

        var isTwoDisc = document.Images.Any(image =>
        {
            var source = image.GetAttribute("src");
            return !string.IsNullOrWhiteSpace(source)
                && source.Contains(TwoDiscImageFileName, StringComparison.OrdinalIgnoreCase);
        });

        return new DiscasDiscDetail(
            title,
            artist,
            rentalStartDate,
            ExtractDescription(normalizedText),
            isTwoDisc,
            ExtractTracks(normalizedText),
            ExtractDetailImageUrl(document, pageUri));
    }

    private static string? ExtractDetailImageUrl(IDocument document, Uri pageUri)
    {
        // 詳細画面の表示用画像は商品ジャケット領域の中だけを対象にする。
        // ページ内にはバナー等の画像も多数あるため、document.Images全体から推測しない。
        var source = document.QuerySelector(".itemJacketWrapOuter #jacketImgM")?.GetAttribute("src")
            ?? document.QuerySelector(".itemJacketWrapOuter img")?.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(source)) return null;

        return Uri.TryCreate(pageUri, source.Trim(), out var imageUri) && imageUri.Scheme is "http" or "https"
            ? imageUri.AbsoluteUri
            : null;
    }

    private static (string Title, string Artist) ParseTitleAndArtist(string? headingText, Uri pageUri)
    {
        var heading = NormalizeText(headingText ?? string.Empty);
        var separatorIndex = heading.LastIndexOf(" / ", StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex + 3 >= heading.Length)
            throw new DiscasDiscDetailParseException($"商品名とアーティストを取得できない: {pageUri}");

        var title = heading[..separatorIndex].Trim();
        var artist = heading[(separatorIndex + 3)..].Trim();
        if (title.Length == 0 || artist.Length == 0)
            throw new DiscasDiscDetailParseException($"商品名とアーティストを取得できない: {pageUri}");

        return (title, artist);
    }

    private static DateOnly? ParseRentalStartDate(string text)
    {
        var match = RentalStartDateRegex().Match(text);
        if (!match.Success) return null;
        return new DateOnly(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
    }

    private static string? ExtractDescription(string text)
    {
        var match = DescriptionRegex().Match(text);
        if (!match.Success) return null;
        var description = match.Groups[1].Value.Trim();
        return description.Length == 0 ? null : description;
    }

    private static IReadOnlyList<ScrapedDiscTrack> ExtractTracks(string text)
    {
        var sectionMatch = TrackSectionRegex().Match(text);
        if (!sectionMatch.Success) return [];

        var tracks = new List<ScrapedDiscTrack>();
        foreach (Match match in TrackRegex().Matches(sectionMatch.Groups[1].Value))
        {
            var trackNumber = int.Parse(match.Groups[1].Value);
            var title = match.Groups[2].Value.Trim();
            var duration = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
            if (title.Length == 0) continue;
            tracks.Add(new ScrapedDiscTrack(trackNumber, title, duration));
        }

        return tracks.GroupBy(x => x.TrackNumber).Select(x => x.First()).OrderBy(x => x.TrackNumber).ToArray();
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

/// <summary>DISCAS詳細ページから取得したCD情報を保持する</summary>
/// <param name="Title">DISCAS詳細ページ上の商品名</param>
/// <param name="Artist">DISCAS詳細ページ上のアーティスト名</param>
/// <param name="RentalStartDate">レンタル開始日</param>
/// <param name="Description">作品詳細。ページに説明がない場合はnull</param>
/// <param name="IsTwoDisc">DISCASの2枚組アイコンが存在するかどうか</param>
/// <param name="Tracks">曲目一覧</param>
/// <param name="DetailImageUrl">詳細画面用のジャケット画像URL。取得できない場合はnull</param>
public sealed record DiscasDiscDetail(string Title, string Artist, DateOnly RentalStartDate, string? Description, bool IsTwoDisc, IReadOnlyList<ScrapedDiscTrack> Tracks, string? DetailImageUrl);

/// <summary>DISCAS詳細ページから取得した1曲分の情報を保持する</summary>
/// <param name="TrackNumber">曲順</param>
/// <param name="Title">曲名</param>
/// <param name="Duration">DISCAS上の演奏時間表記。取得できない場合はnull</param>
public sealed record ScrapedDiscTrack(int TrackNumber, string Title, string? Duration);

/// <summary>DISCAS詳細ページを安全に解析できない場合に発生する例外</summary>
public sealed class DiscasDiscDetailParseException : Exception
{
    /// <summary>解析失敗理由を指定して例外を初期化する</summary>
    /// <param name="message">解析失敗の概要</param>
    public DiscasDiscDetailParseException(string message) : base(message) { }
}
