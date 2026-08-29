using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Web.Pages;

/// <summary>
/// CD単体のメタデータ、取得元、変更履歴、Artist Watch状態とレビュー操作を提供する
/// </summary>
public sealed class DiscDetailModel(
    DiscaScoutDbContext dbContext,
    DiscDetailFetchSignal detailFetchSignal) : PageModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>表示対象CD</summary>
    public Disc Disc { get; private set; } = null!;

    /// <summary>一覧へ戻るためのローカルURL</summary>
    [BindProperty(SupportsGet = true, Name = "returnUrl")]
    public string? ReturnUrl { get; set; }

    /// <summary>処理結果メッセージ</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// 指定CDの詳細情報を読み込む
    /// </summary>
    /// <param name="id">Discの内部ID</param>
    /// <param name="cancellationToken">処理を中断するためのトークン</param>
    public async Task<IActionResult> OnGetAsync(long id, CancellationToken cancellationToken)
    {
        var disc = await LoadDiscAsync(id, cancellationToken);
        if (disc is null)
        {
            return NotFound();
        }

        // DISCAS側の作品詳細は段落や改行がほぼなく、そのままでは長文を追いにくい。
        // 保存値は原文のまま維持し、詳細画面の表示モデルだけ句点ごとに改行して可読性を補う。
        if (!string.IsNullOrWhiteSpace(disc.Description))
        {
            disc.Description = disc.Description.Replace("。", $"。{Environment.NewLine}", StringComparison.Ordinal);
        }

        Disc = disc;
        if (!disc.DetailRefreshCompleted)
        {
            // 詳細画面で実際に参照されたCDは、全件を順番に補完するBackgroundServiceより先に確認したい。
            // Web要求自体ではDISCASへアクセスせず、優先キューへ通知するだけにして画面表示を待たせない。
            detailFetchSignal.Request(id);
        }

        return Page();
    }

    /// <summary>
    /// CDを確認済みにし、現在のレビュー理由を解消する
    /// </summary>
    public async Task<IActionResult> OnPostReviewedAsync(long id, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs
            .Include(x => x.ReviewReasons)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null)
        {
            return NotFound();
        }

        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = "確認済みにしました";
        return RedirectToDetail(id);
    }

    /// <summary>
    /// 確認済みCDを手動で未チェックへ戻す
    /// </summary>
    public async Task<IActionResult> OnPostReopenAsync(long id, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null)
        {
            return NotFound();
        }

        if (!disc.IsRented)
        {
            // 手動での再確認要求は自動差分理由ではないため、ReviewReasonを捏造せずNeedsReviewだけを戻す。
            disc.NeedsReview = true;
            await dbContext.SaveChangesAsync(cancellationToken);
            StatusMessage = "未チェックへ戻しました";
        }

        return RedirectToDetail(id);
    }

    /// <summary>
    /// CDを借りた状態にし、現在のレビュー理由を解消する
    /// </summary>
    public async Task<IActionResult> OnPostRentedAsync(long id, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs
            .Include(x => x.ReviewReasons)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null)
        {
            return NotFound();
        }

        disc.IsRented = true;
        disc.NeedsReview = false;
        disc.LastReviewedAt = TimeProvider.System.GetUtcNow().UtcDateTime;
        dbContext.DiscReviewReasons.RemoveRange(disc.ReviewReasons);
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = "借りた状態にしました";
        return RedirectToDetail(id);
    }

    /// <summary>
    /// CDを未レンタル状態へ戻す
    /// </summary>
    /// <remarks>
    /// レンタル済みにした時点でレビュー理由は解消済みなので、未レンタルへ戻しても自動的には未チェックへ戻さない。
    /// 再確認が必要な場合は利用者が別途「未チェックへ戻す」を実行する。
    /// </remarks>
    public async Task<IActionResult> OnPostUnrentedAsync(long id, CancellationToken cancellationToken)
    {
        var disc = await dbContext.Discs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (disc is null)
        {
            return NotFound();
        }

        disc.IsRented = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        StatusMessage = "未レンタル状態へ戻しました";
        return RedirectToDetail(id);
    }

    /// <summary>
    /// UTCで保存された日時を日本時間の表示文字列へ変換する
    /// </summary>
    public static string FormatJapanTime(DateTime value) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), JapanTimeZone)
            .ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// nullableなUTC日時を日本時間の表示文字列へ変換する
    /// </summary>
    public static string FormatJapanTime(DateTime? value) => value is null ? "-" : FormatJapanTime(value.Value);

    /// <summary>
    /// 詳細情報の取得状態を表示用文字列へ変換する
    /// </summary>
    public static string FormatDetailStatus(Disc disc)
    {
        if (disc.DetailRefreshCompleted)
        {
            return "取得完了";
        }

        if (disc.DetailFetchedAt is null)
        {
            return "未取得（バックグラウンド取得待ち）";
        }

        return "取得済み（レンタル開始後に最終確認予定）";
    }

    /// <summary>
    /// 2枚組判定を表示用文字列へ変換する
    /// </summary>
    public static string FormatTwoDisc(bool? isTwoDisc) => isTwoDisc switch
    {
        true => "2枚組",
        false => "2枚組ではない",
        null => "未取得"
    };

    /// <summary>
    /// ReviewReasonを日本語表示へ変換する
    /// </summary>
    public static string FormatReviewReason(DiscReviewReasonType reason) => reason switch
    {
        DiscReviewReasonType.New => "新規",
        DiscReviewReasonType.TitleChanged => "タイトル変更",
        DiscReviewReasonType.ArtistMatched => "Artist Watch一致",
        DiscReviewReasonType.Reappeared => "再出現",
        _ => reason.ToString()
    };

    /// <summary>
    /// リリースカテゴリを日本語表示へ変換する
    /// </summary>
    public static string FormatCategory(DiscReleaseCategory category) => category switch
    {
        DiscReleaseCategory.Upcoming => "近日リリース",
        DiscReleaseCategory.New => "新作",
        _ => category.ToString()
    };

    private async Task<Disc?> LoadDiscAsync(long id, CancellationToken cancellationToken)
    {
        return await dbContext.Discs
            .AsNoTracking()
            .AsSplitQuery()
            .Include(x => x.Sources)
            .Include(x => x.ReviewReasons)
            .Include(x => x.ChangeHistory)
            .Include(x => x.Tracks)
            .Include(x => x.ArtistMatches)
                .ThenInclude(x => x.ArtistSetting)
            .Include(x => x.ArtistCatalogEntries)
                .ThenInclude(x => x.ArtistSetting)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    private IActionResult RedirectToDetail(long id)
    {
        return RedirectToPage("/DiscDetail", new { id, returnUrl = ReturnUrl });
    }
}
