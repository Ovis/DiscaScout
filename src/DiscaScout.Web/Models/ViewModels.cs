using DiscaScout.Core;
using Microsoft.AspNetCore.WebUtilities;

namespace DiscaScout.Web.Models;

/// <summary>
/// CD一覧画面へ渡す検索条件・件数・表示対象CDを保持する
/// </summary>
public sealed class DiscsViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public string Tab { get; init; } = "unchecked";
    public string? TitleSearch { get; init; }
    public string? ArtistSearch { get; init; }
    public string? Genre { get; init; }
    public bool ExcludeMaxi { get; init; }
    public bool ExcludeAlbum { get; init; }
    public string Rental { get; init; } = "all";
    public string Sort { get; init; } = "updated";
    public int PageSize { get; init; } = 50;
    public int PageNumber { get; init; } = 1;
    public IReadOnlyList<Disc> Items { get; init; } = [];
    public IReadOnlyList<GenreGroup> GenreGroups { get; init; } = [];
    public int UncheckedCount { get; init; }
    public int PickupCount { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string? StatusMessage { get; init; }
    public string? UndoPayload { get; init; }

    /// <summary>Pickup対象となっているArtist設定名を重複なしで表示する</summary>
    public static string GetPickupArtists(Disc disc) => string.Join(", ", disc.ArtistMatches
        .Where(x => x.IsCurrentMatch && !x.ArtistSetting.IsArchived && x.ArtistSetting.IsWatchEnabled)
        .Select(x => x.ArtistSetting.Artist)
        .Distinct(StringComparer.Ordinal));

    /// <summary>レビュー理由を日本語表示へ変換する</summary>
    public static string FormatReviewReason(DiscReviewReasonType reason) => reason switch
    {
        DiscReviewReasonType.New => "新規",
        DiscReviewReasonType.TitleChanged => "タイトル変更",
        DiscReviewReasonType.ArtistMatched => "Artist Watch一致",
        DiscReviewReasonType.Reappeared => "再出現",
        _ => reason.ToString()
    };

    /// <summary>レンタル開始日から現在のDISCASレンタル区分を求める</summary>
    public static string? GetRentalCategoryLabel(DateOnly? rentalStartDate)
    {
        if (rentalStartDate is null) return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TimeProvider.System.GetUtcNow(), JapanTimeZone).DateTime);
        var elapsedDays = today.DayNumber - rentalStartDate.Value.DayNumber;
        if (elapsedDays < 0) return "近日リリース";
        if (elapsedDays <= 90) return "新作";
        if (elapsedDays <= 180) return "準新作";
        return "旧作";
    }

    public string GetTabUrl(string tab) => BuildListUrl(tab, TitleSearch, ArtistSearch, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetArtistUrl(string artist) => BuildListUrl(Tab, TitleSearch, artist, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetGenreUrl(string genre) => BuildListUrl(Tab, TitleSearch, ArtistSearch, genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetPageUrl(int page) => BuildListUrl(Tab, TitleSearch, ArtistSearch, Genre, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, page);

    private static string BuildListUrl(string tab, string? title, string? artist, string? genre, bool excludeMaxi, bool excludeAlbum, string rental, string sort, int size, int page)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["tab"] = tab,
            ["title"] = title,
            ["artist"] = artist,
            ["genre"] = genre,
            ["excludeMaxi"] = excludeMaxi ? "true" : null,
            ["excludeAlbum"] = excludeAlbum ? "true" : null,
            ["rental"] = rental == "all" ? null : rental,
            ["sort"] = sort == "updated" ? null : sort,
            ["size"] = size == 50 ? null : size.ToString(),
            ["p"] = page <= 1 ? null : page.ToString()
        };
        return QueryHelpers.AddQueryString("/discs", parameters.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value));
    }

    public sealed record GenreGroup(string Name, IReadOnlyList<GenreMiddleGroup> MiddleGenres);
    public sealed record GenreMiddleGroup(string Name, IReadOnlyList<string> SmallGenres);
}

/// <summary>
/// CD詳細画面へ渡すCD情報と一覧復帰先を保持する
/// </summary>
public sealed class DiscDetailViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public required Disc Disc { get; init; }
    public string? ReturnUrl { get; init; }
    public string? StatusMessage { get; init; }

    public static string FormatJapanTime(DateTime value) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone).ToString("yyyy-MM-dd HH:mm:ss");
    public static string FormatJapanTime(DateTime? value) => value is null ? "-" : FormatJapanTime(value.Value);

    /// <summary>DISCASのCDレンタル経過日数定義に従って現在の新作区分を求める</summary>
    public static string? FormatRentalCategory(DateOnly? rentalStartDate)
    {
        if (rentalStartDate is null) return null;
        var todayInJapan = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, JapanTimeZone).DateTime);
        var elapsedDays = todayInJapan.DayNumber - rentalStartDate.Value.DayNumber;
        if (elapsedDays < 0) return "近日リリース";
        if (elapsedDays <= 90) return "新作";
        if (elapsedDays <= 180) return "準新作";
        return "旧作";
    }

    public static string FormatDetailStatus(Disc disc)
    {
        if (disc.DetailRefreshCompleted) return "取得完了";
        if (disc.DetailFetchedAt is null) return "未取得（バックグラウンド取得待ち）";
        return "取得済み（レンタル開始後に最終確認予定）";
    }

    public static string FormatTwoDisc(bool? isTwoDisc) => isTwoDisc switch
    {
        true => "2枚組",
        false => "2枚組ではない",
        null => "未取得"
    };

    public static string FormatReviewReason(DiscReviewReasonType reason) => reason switch
    {
        DiscReviewReasonType.New => "新規",
        DiscReviewReasonType.TitleChanged => "タイトル変更",
        DiscReviewReasonType.ArtistMatched => "Artist Watch一致",
        DiscReviewReasonType.Reappeared => "再出現",
        _ => reason.ToString()
    };

    public static string FormatCategory(DiscReleaseCategory category) => category switch
    {
        DiscReleaseCategory.Upcoming => "近日リリース",
        DiscReleaseCategory.New => "新作",
        _ => category.ToString()
    };
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

    public sealed record ArtistSettingRow(long Id, string Artist, ArtistMatchType MatchType, bool IsWatchEnabled, bool CollectFullCatalog, bool ReviewInitialCatalogItems, bool InitialCatalogCollectionCompleted, bool IsArchived, int CurrentMatchCount, int ActiveCatalogCount);
    public sealed record ArtistSettingPreview(long? Id, string Artist, ArtistMatchType MatchType, bool IsWatchEnabled, bool CollectFullCatalog, bool ReviewInitialCatalogItems, int MatchCount, int ReviewedMatchCount, int NewlyMatchedCount, int ReopenCandidateCount);
}

/// <summary>
/// 運用画面へ渡す定期実行設定と実行状態を保持する
/// </summary>
public sealed class OperationsViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public bool IsEnabled { get; init; }
    public DayOfWeek DayOfWeek { get; init; }
    public TimeOnly LocalTime { get; init; }
    public DateOnly? LastScheduledExecutionDate { get; init; }
    public IReadOnlyList<ScrapeRun> RecentRuns { get; init; } = [];
    public IReadOnlyList<ScrapeRetry> PendingRetries { get; init; } = [];
    public IReadOnlyList<ManualWorkItem> ActiveManualWork { get; init; } = [];
    public IReadOnlyList<ManualWorkItem> RecentManualWork { get; init; } = [];
    public string? StatusMessage { get; init; }
    public bool IsFullScrapeActive => ActiveManualWork.Any(x => x.Type is ManualWorkType.FullScrape or ManualWorkType.CategoryScrape);

    public static IReadOnlyList<(DayOfWeek Value, string Label)> DayOptions { get; } =
    [
        (System.DayOfWeek.Monday, "月曜日"), (System.DayOfWeek.Tuesday, "火曜日"),
        (System.DayOfWeek.Wednesday, "水曜日"), (System.DayOfWeek.Thursday, "木曜日"),
        (System.DayOfWeek.Friday, "金曜日"), (System.DayOfWeek.Saturday, "土曜日"),
        (System.DayOfWeek.Sunday, "日曜日")
    ];

    public static DateTime ToJapanTime(DateTime value) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone);
    public static string GetDayLabel(DayOfWeek value) => DayOptions.First(x => x.Value == value).Label;
    public static string GetManualWorkTypeLabel(ManualWorkType type) => type switch
    {
        ManualWorkType.FullScrape => "通常取得",
        ManualWorkType.CategoryScrape => "カテゴリ取得",
        ManualWorkType.ArtistCatalog => "Artist全作品",
        _ => type.ToString()
    };
    public static string GetManualWorkTarget(ManualWorkItem work) => work.Type switch
    {
        ManualWorkType.CategoryScrape => work.Category?.ToString() ?? "-",
        ManualWorkType.ArtistCatalog => work.ArtistSettingId?.ToString() ?? "-",
        _ => "-"
    };
}

/// <summary>
/// 設定画面へ渡すDiscord設定とスクレイピング安全装置状態を保持する
/// </summary>
public sealed class SettingsViewModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    public DiscordNotificationMode DiscordMode { get; init; }
    public string? DiscordWebhookUrl { get; init; }
    public string? StatusMessage { get; init; }
    public IReadOnlyList<ScrapeGuardStatus> ScrapeGuards { get; init; } = [];
    public ScrapeGuardStatus? CountDropConfirmation { get; init; }

    public static DateTime ToJapanTime(DateTime value) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone);
    public static string GetCategoryLabel(ScrapeCategory category) => category switch
    {
        ScrapeCategory.Upcoming => "近日リリース",
        ScrapeCategory.New => "新作",
        _ => category.ToString()
    };

    public sealed record ScrapeGuardStatus(ScrapeCategory Category, ScrapeGuardSettings Settings, ScrapeRun? Baseline, ScrapeRun? LatestAnomaly);
}
