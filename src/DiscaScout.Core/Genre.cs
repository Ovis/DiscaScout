namespace DiscaScout.Core;

/// <summary>
/// DISCASのジャンルマスターに含まれる1ノードを保持する
/// </summary>
public sealed class Genre
{
    public long Id { get; set; }
    public required string ExternalId { get; set; }
    public required string Name { get; set; }
    public long? ParentId { get; set; }
    public Genre? Parent { get; set; }
    public List<Genre> Children { get; } = [];
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public List<Disc> Discs { get; } = [];
}

/// <summary>
/// ジャンルマスター自体の最終正常更新日時を保持する
/// </summary>
public sealed class GenreMasterState
{
    public int Id { get; set; }
    public DateTime? LastUpdatedAt { get; set; }
}
