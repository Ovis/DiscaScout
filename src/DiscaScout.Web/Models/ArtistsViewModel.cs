using DiscaScout.Core;

namespace DiscaScout.Web.Models;

/// <summary>
/// Artist追加時の取得方法を表す
/// </summary>
public enum ArtistAcquisitionMode
{
    SaveSetting = 0,
    OneShot = 1
}

/// <summary>
/// Artist設定画面へ渡す設定一覧と保存前プレビューを保持する
/// </summary>
public sealed class ArtistsViewModel
{
    public IReadOnlyList<ArtistSettingRow> Settings { get; init; } = [];
    public HashSet<long> ActiveCatalogSettingIds { get; init; } = [];
    public ArtistSettingPreview? Preview { get; init; }
    public string? StatusMessage { get; init; }

    public sealed record ArtistSettingRow(
        long Id,
        string Artist,
        ArtistMatchType MatchType,
        bool IsWatchEnabled,
        bool CollectFullCatalog,
        bool ReviewInitialCatalogItems,
        bool InitialCatalogCollectionCompleted,
        bool IsArchived,
        int CurrentMatchCount,
        int ActiveCatalogCount);

    public sealed record ArtistSettingPreview(
        long? Id,
        string Artist,
        ArtistMatchType MatchType,
        ArtistAcquisitionMode AcquisitionMode,
        bool IsWatchEnabled,
        bool CollectFullCatalog,
        bool ReviewInitialCatalogItems,
        bool ReviewOneShotItems,
        int MatchCount,
        int ReviewedMatchCount,
        int NewlyMatchedCount,
        int ReopenCandidateCount);
}
