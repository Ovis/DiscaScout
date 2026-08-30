using DiscaScout.Core;

namespace DiscaScout.Web.Models;

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

    /// <summary>読み込み済みGenreの親参照から表示用のルート順パスを組み立てる</summary>
    public static string FormatGenrePath(Disc disc)
    {
        if (disc.Genre is null) return "未解決";
        var names = new List<string>();
        for (var current = disc.Genre; current is not null; current = current.Parent)
            names.Add(current.Name);
        names.Reverse();
        return string.Join(" / ", names);
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
