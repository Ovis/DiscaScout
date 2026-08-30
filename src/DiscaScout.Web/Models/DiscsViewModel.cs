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
    public string UncheckedFilter { get; init; } = "all";
    public string? TitleSearch { get; init; }
    public bool SearchDescription { get; init; }
    public bool SearchTracks { get; init; }
    public string? ArtistSearch { get; init; }
    public long? GenreLargeId { get; init; }
    public long? GenreMiddleId { get; init; }
    public long? GenreSmallId { get; init; }
    public bool ExcludeMaxi { get; init; }
    public bool ExcludeAlbum { get; init; }
    public string Rental { get; init; } = "all";
    public string Sort { get; init; } = "updated";
    public int PageSize { get; init; } = 50;
    public int PageNumber { get; init; } = 1;
    public IReadOnlyList<Disc> Items { get; init; } = [];
    public IReadOnlyList<GenreOption> GenreGroups { get; init; } = [];
    public int UncheckedCount { get; init; }
    public int PickupCount { get; init; }
    public int RentedCount { get; init; }
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

    /// <summary>読み込み済みの親参照からルート順のジャンルパスを返す</summary>
    public static IReadOnlyList<Genre> GetGenrePath(Disc disc)
    {
        if (disc.Genre is null) return [];
        var result = new List<Genre>();
        for (var current = disc.Genre; current is not null; current = current.Parent)
            result.Add(current);
        result.Reverse();
        return result;
    }

    public string GetTabUrl(string tab) => BuildListUrl(tab, tab == "unchecked" ? UncheckedFilter : "all", TitleSearch, SearchDescription, SearchTracks, ArtistSearch, GenreLargeId, GenreMiddleId, GenreSmallId, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetArtistUrl(string artist) => BuildListUrl(Tab, UncheckedFilter, TitleSearch, SearchDescription, SearchTracks, artist, GenreLargeId, GenreMiddleId, GenreSmallId, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    public string GetPageUrl(int page) => BuildListUrl(Tab, UncheckedFilter, TitleSearch, SearchDescription, SearchTracks, ArtistSearch, GenreLargeId, GenreMiddleId, GenreSmallId, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, page);

    /// <summary>カード上のジャンルノードを階層選択として反映するURLを生成する</summary>
    public string GetGenreUrl(IReadOnlyList<Genre> path, int selectedDepth)
    {
        var large = selectedDepth >= 0 ? path.ElementAtOrDefault(0)?.Id : null;
        var middle = selectedDepth >= 1 ? path.ElementAtOrDefault(1)?.Id : null;
        var small = selectedDepth >= 2 ? path.ElementAtOrDefault(2)?.Id : null;
        return BuildListUrl(Tab, UncheckedFilter, TitleSearch, SearchDescription, SearchTracks, ArtistSearch, large, middle, small, ExcludeMaxi, ExcludeAlbum, Rental, Sort, PageSize, 1);
    }

    private static string BuildListUrl(
        string tab,
        string uncheckedFilter,
        string? title,
        bool searchDescription,
        bool searchTracks,
        string? artist,
        long? genreLargeId,
        long? genreMiddleId,
        long? genreSmallId,
        bool excludeMaxi,
        bool excludeAlbum,
        string rental,
        string sort,
        int size,
        int page)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["tab"] = tab,
            ["uncheckedFilter"] = tab == "unchecked" && uncheckedFilter != "all" ? uncheckedFilter : null,
            ["title"] = title,
            ["searchDescription"] = searchDescription ? "true" : null,
            ["searchTracks"] = searchTracks ? "true" : null,
            ["artist"] = artist,
            ["genreLarge"] = genreLargeId?.ToString(),
            ["genreMiddle"] = genreMiddleId?.ToString(),
            ["genreSmall"] = genreSmallId?.ToString(),
            ["excludeMaxi"] = excludeMaxi ? "true" : null,
            ["excludeAlbum"] = excludeAlbum ? "true" : null,
            ["rental"] = rental == "all" ? null : rental,
            ["sort"] = sort == "updated" ? null : sort,
            ["size"] = size == 50 ? null : size.ToString(),
            ["p"] = page <= 1 ? null : page.ToString()
        };
        return QueryHelpers.AddQueryString("/discs", parameters.Where(x => x.Value is not null).ToDictionary(x => x.Key, x => x.Value));
    }

    /// <summary>一覧フィルター用ジャンルノードを保持する</summary>
    public sealed record GenreOption(long Id, string Name, bool IsActive, IReadOnlyList<GenreOption> Children);
}
