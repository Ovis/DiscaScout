namespace DiscaScout.Core;

/// <summary>
/// アーティスト名とCDアーティスト表記の一致方法を表す
/// </summary>
public enum ArtistMatchType
{
    Exact = 1,
    Contains = 2
}

/// <summary>
/// Artist Watchと全作品収集で共有するアーティスト設定を保持する
/// </summary>
public sealed class ArtistSetting
{
    public long Id { get; set; }
    public required string Artist { get; set; }
    public required string NormalizedArtist { get; set; }
    public ArtistMatchType MatchType { get; set; } = ArtistMatchType.Exact;
    public bool IsWatchEnabled { get; set; } = true;
    public bool CollectFullCatalog { get; set; }
    public bool IsArchived { get; set; }
    public List<DiscArtistMatch> DiscMatches { get; } = [];
    public List<DiscArtistCatalog> CatalogEntries { get; } = [];
}

/// <summary>
/// CDとアーティスト設定の現在・過去の一致状態を保持する
/// </summary>
public sealed class DiscArtistMatch
{
    public long Id { get; set; }
    public long DiscId { get; set; }
    public Disc Disc { get; set; } = null!;
    public long ArtistSettingId { get; set; }
    public ArtistSetting ArtistSetting { get; set; } = null!;
    public bool IsCurrentMatch { get; set; }
    public DateTime FirstMatchedAt { get; set; }
    public DateTime LastMatchedAt { get; set; }
    public DateTime? LastUnmatchedAt { get; set; }
}

/// <summary>
/// 全作品収集で取得したCDとアーティスト設定の所属関係を保持する
/// </summary>
public sealed class DiscArtistCatalog
{
    public long Id { get; set; }
    public long DiscId { get; set; }
    public Disc Disc { get; set; } = null!;
    public long ArtistSettingId { get; set; }
    public ArtistSetting ArtistSetting { get; set; } = null!;
    public bool IsActive { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }
}

/// <summary>
/// Artist Watchの文字列一致判定を提供する
/// </summary>
public static class ArtistWatchMatcher
{
    /// <summary>
    /// 正規化済みアーティスト名が設定条件に一致するか判定する
    /// </summary>
    /// <param name="normalizedDiscArtist">CD側の正規化済みアーティスト名</param>
    /// <param name="setting">判定対象のアーティスト設定</param>
    public static bool IsMatch(string normalizedDiscArtist, ArtistSetting setting)
    {
        ArgumentNullException.ThrowIfNull(normalizedDiscArtist);
        ArgumentNullException.ThrowIfNull(setting);

        return setting.MatchType switch
        {
            ArtistMatchType.Exact => string.Equals(
                normalizedDiscArtist,
                setting.NormalizedArtist,
                StringComparison.Ordinal),
            ArtistMatchType.Contains => normalizedDiscArtist.Contains(
                setting.NormalizedArtist,
                StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(setting.MatchType), setting.MatchType, null)
        };
    }
}
