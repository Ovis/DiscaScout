using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace DiscaScout.Scraping;

/// <summary>
/// TSUTAYA DISCASのCD検索結果HTMLから商品情報とページ情報を抽出する
/// </summary>
public sealed partial class DiscasSearchResultParser
{
    private const string ProductSelector = ".cd-product-item";
    private const string TitleSelector = ".card-title-searchCd";
    private const string ArtistSelector = ".card-body-searchCd a[href*='artistsearchHmo.do']";
    private const string ImageSelector = ".card-img";
    private const string NoImagePath = "/img/jacket/no_image_cd_s.png";

    private readonly HtmlParser htmlParser = new();

    /// <summary>
    /// 検索結果1ページを解析する
    /// </summary>
    /// <param name="html">DISCASから取得してデコード済みのHTML</param>
    /// <param name="pageUri">解析対象ページのURL。相対URLの解決に使用する</param>
    /// <param name="category">このページを取得したリリースカテゴリ</param>
    /// <param name="sourceRankOffset">このページより前に存在する商品数。1ページ目では0</param>
    /// <returns>商品一覧と検索結果全体の件数を含む解析結果</returns>
    public DiscasSearchPage Parse(
        string html,
        Uri pageUri,
        DiscSourceCategory category,
        int sourceRankOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUri);

        if (!pageUri.IsAbsoluteUri)
        {
            throw new ArgumentException("ページURLには絶対URLを指定する必要がある", nameof(pageUri));
        }

        if (sourceRankOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRankOffset));
        }

        var document = htmlParser.ParseDocument(html);
        var productElements = document.QuerySelectorAll(ProductSelector);
        var products = new List<ScrapedDisc>(productElements.Length);

        for (var index = 0; index < productElements.Length; index++)
        {
            var productElement = productElements[index];
            var titleLink = productElement.QuerySelector(TitleSelector)
                ?? throw new DiscasSearchParseException($"商品{index + 1}にタイトルリンクが存在しない");

            var href = titleLink.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                throw new DiscasSearchParseException($"商品{index + 1}のタイトルリンクにhrefが存在しない");
            }

            var productUri = new Uri(pageUri, href);
            var discasId = ExtractTitleId(productUri)
                ?? throw new DiscasSearchParseException($"商品{index + 1}の商品URLからtitleIDを取得できない: {productUri}");

            var title = NormalizeDisplayText(titleLink.TextContent);
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new DiscasSearchParseException($"商品{index + 1}のタイトルが空である");
            }

            var artistLink = productElement.QuerySelector(ArtistSelector)
                ?? throw new DiscasSearchParseException($"商品{index + 1}にアーティストリンクが存在しない");
            var artist = NormalizeDisplayText(artistLink.TextContent);
            if (string.IsNullOrWhiteSpace(artist))
            {
                throw new DiscasSearchParseException($"商品{index + 1}のアーティストが空である");
            }

            var imageUrl = ResolveImageUrl(productElement.QuerySelector(ImageSelector)?.GetAttribute("src"), pageUri);

            products.Add(new ScrapedDisc(
                discasId,
                productUri.AbsoluteUri,
                title,
                artist,
                imageUrl,
                RentalStartDate: null,
                category,
                SourceRank: sourceRankOffset + index + 1));
        }

        var totalCount = ParseTotalCount(document.QuerySelector(".pagination-cd-search p")?.TextContent);
        var hiddenTitleIds = ParseHiddenTitleIds(document.QuerySelector("input[name='titleId']")?.GetAttribute("value"));

        return new DiscasSearchPage(products, totalCount, hiddenTitleIds);
    }

    /// <summary>
    /// 商品詳細URLからtitleIDを抽出する
    /// </summary>
    /// <param name="productUri">DISCASの商品詳細URL</param>
    /// <returns>titleID。存在しない場合はnull</returns>
    internal static string? ExtractTitleId(Uri productUri)
    {
        var match = TitleIdRegex().Match(productUri.Query);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static string NormalizeDisplayText(string text)
    {
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private static string? ResolveImageUrl(string? source, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var imageUri = new Uri(pageUri, source.Trim());

        // DISCASは画像未登録商品にも共通のプレースホルダー画像を返す。
        // これを商品画像として保存すると後から実画像が追加されたか判定しづらくなるため、未登録として扱う。
        if (imageUri.AbsolutePath.EndsWith(NoImagePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return imageUri.AbsoluteUri;
    }

    private static int? ParseTotalCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = TotalCountRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var normalizedCount = match.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(normalizedCount, out var totalCount) ? totalCount : null;
    }

    private static IReadOnlyList<string> ParseHiddenTitleIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    [GeneratedRegex(@"(?:^|[?&])titleID=([^&]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdRegex();

    [GeneratedRegex(@"全\s*([0-9,]+)\s*件", RegexOptions.CultureInvariant)]
    private static partial Regex TotalCountRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

/// <summary>
/// DISCAS検索結果1ページの解析結果を保持する
/// </summary>
/// <param name="Products">ページ内の商品一覧</param>
/// <param name="TotalCount">検索結果全体の件数。ページから取得できない場合はnull</param>
/// <param name="HiddenTitleIds">ページ内hidden fieldに列挙されたtitleID</param>
public sealed record DiscasSearchPage(
    IReadOnlyList<ScrapedDisc> Products,
    int? TotalCount,
    IReadOnlyList<string> HiddenTitleIds);

/// <summary>
/// DISCAS検索結果が想定した商品DOMを満たさず、安全に解析できない場合に発生する例外
/// </summary>
public sealed class DiscasSearchParseException : Exception
{
    /// <summary>
    /// 解析失敗理由を指定して例外を初期化する
    /// </summary>
    /// <param name="message">解析失敗の概要</param>
    public DiscasSearchParseException(string message)
        : base(message)
    {
    }
}
