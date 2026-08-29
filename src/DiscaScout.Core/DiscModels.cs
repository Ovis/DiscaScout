using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscaScout.Core;

/// <summary>
/// DISCAS上で観測したCDと、DiscaScout内で管理する状態を保持する
/// </summary>
public sealed class Disc
{
    public long Id { get; set; }
    public required string DiscasId { get; set; }
    public required string ProductUrl { get; set; }
    public required string Title { get; set; }
    public required string NormalizedTitle { get; set; }
    public required string Artist { get; set; }
    public required string NormalizedArtist { get; set; }
    public required string GenreLarge { get; set; }
    public string? GenreMiddle { get; set; }
    public string? GenreSmall { get; set; }
    public string? ImageUrl { get; set; }
    public string? ImagePath { get; set; }
    public DateOnly? RentalStartDate { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset LastUpdatedAt { get; set; }
    public bool IsArchived { get; set; }
    public bool NeedsReview { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
    public bool IsRented { get; set; }
    public List<DiscSource> Sources { get; } = [];
    public List<DiscReviewReason> ReviewReasons { get; } = [];
    public List<DiscChangeHistory> ChangeHistory { get; } = [];
}

/// <summary>
/// CDがどのリリースカテゴリで現在観測されているかを保持する
/// </summary>
public sealed class DiscSource
{
    public long Id { get; set; }
    public long DiscId { get; set; }
    public Disc Disc { get; set; } = null!;
    public DiscReleaseCategory Category { get; set; }
    public int SourceRank { get; set; }
    public bool IsActive { get; set; }
    public int MissingCount { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>
/// InboxでCDを再確認する必要が生じた現在の理由を保持する
/// </summary>
public sealed class DiscReviewReason
{
    public long Id { get; set; }
    public long DiscId { get; set; }
    public Disc Disc { get; set; } = null!;
    public DiscReviewReasonType Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// CDメタデータの意味のある変更履歴を保持する
/// </summary>
public sealed class DiscChangeHistory
{
    public long Id { get; set; }
    public long DiscId { get; set; }
    public Disc Disc { get; set; } = null!;
    public required string Field { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>
/// 通常スクレイピングで扱うDISCASのリリースカテゴリ
/// </summary>
public enum DiscReleaseCategory
{
    Upcoming = 1,
    New = 2
}

/// <summary>
/// CDを未チェック状態にする理由
/// </summary>
public enum DiscReviewReasonType
{
    New = 1,
    TitleChanged = 2,
    ArtistMatched = 3,
    Reappeared = 4
}

/// <summary>
/// DISCAS由来文字列の比較に使用する共通正規化を提供する
/// </summary>
public static partial class DiscTextNormalizer
{
    /// <summary>
    /// 表示文字列をNFKC・空白統合・大文字化して比較用文字列へ変換する
    /// </summary>
    /// <param name="value">正規化対象の表示文字列</param>
    /// <returns>意味比較に使用する正規化済み文字列</returns>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = value.Normalize(NormalizationForm.FormKC);
        normalized = WhitespaceRegex().Replace(normalized, " ").Trim();
        return normalized.ToUpper(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
