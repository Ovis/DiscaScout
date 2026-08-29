using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DiscaScout.Web.Pages;

/// <summary>
/// 定期取得設定、手動実行、実行履歴を管理する運用画面
/// </summary>
public sealed class OperationsModel(
    IScrapeScheduleStore scheduleStore,
    IScrapeOperationsStore operationsStore,
    ScrapeExecutionGate executionGate,
    ScrapeRunCoordinator coordinator) : PageModel
{
    private static readonly TimeZoneInfo JapanTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    /// <summary>画面で編集する定期実行の有効状態</summary>
    [BindProperty]
    public bool IsEnabled { get; set; }

    /// <summary>画面で編集する曜日</summary>
    [BindProperty]
    public DayOfWeek DayOfWeek { get; set; }

    /// <summary>画面で編集する日本時間の実行時刻</summary>
    [BindProperty]
    public TimeOnly LocalTime { get; set; }

    /// <summary>最後に定期実行した日本時間の日付</summary>
    public DateOnly? LastScheduledExecutionDate { get; private set; }

    /// <summary>直近の実行履歴</summary>
    public IReadOnlyList<ScrapeRun> RecentRuns { get; private set; } = [];

    /// <summary>現在保留中のRetry</summary>
    public IReadOnlyList<ScrapeRetry> PendingRetries { get; private set; } = [];

    /// <summary>処理後に表示する短いメッセージ</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>曜日選択肢</summary>
    public static IReadOnlyList<(DayOfWeek Value, string Label)> DayOptions { get; } =
    [
        (System.DayOfWeek.Monday, "月曜日"),
        (System.DayOfWeek.Tuesday, "火曜日"),
        (System.DayOfWeek.Wednesday, "水曜日"),
        (System.DayOfWeek.Thursday, "木曜日"),
        (System.DayOfWeek.Friday, "金曜日"),
        (System.DayOfWeek.Saturday, "土曜日"),
        (System.DayOfWeek.Sunday, "日曜日")
    ];

    /// <summary>
    /// 現在の設定と運用状態を表示する
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    /// <summary>
    /// 定期実行設定を保存する
    /// </summary>
    public async Task<IActionResult> OnPostSaveScheduleAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(DayOfWeek))
        {
            ModelState.AddModelError(nameof(DayOfWeek), "曜日が不正です");
        }

        if (!ModelState.IsValid)
        {
            await LoadOperationalStateAsync(cancellationToken);
            return Page();
        }

        await scheduleStore.UpdateAsync(IsEnabled, DayOfWeek, LocalTime, cancellationToken);
        StatusMessage = IsEnabled
            ? $"定期取得を {GetDayLabel(DayOfWeek)} {LocalTime:HH\\:mm} に設定しました"
            : "定期取得を無効にしました";
        return RedirectToPage();
    }

    /// <summary>
    /// UpcomingとNewを手動実行する
    /// </summary>
    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken cancellationToken)
    {
        // 手動実行もBackgroundServiceと同じ排他を利用し、定期実行やRetryと重複して
        // DISCASへアクセスしないようにする。
        var result = await executionGate.TryRunAsync(
            ct => coordinator.ExecuteAsync(ScrapeExecutionType.Manual, ct),
            cancellationToken);

        StatusMessage = result is null
            ? "別の取得処理が実行中のため、手動取得は開始しませんでした"
            : result.IsSuccess
                ? "手動取得が正常に完了しました"
                : "手動取得は完了しましたが、失敗したカテゴリがあります。実行履歴を確認してください";

        return RedirectToPage();
    }

    /// <summary>
    /// UTCの履歴時刻を運用画面用の日本時間へ変換する
    /// </summary>
    /// <param name="value">保存されている時刻</param>
    /// <returns>日本時間</returns>
    public static DateTimeOffset ToJapanTime(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, JapanTimeZone);

    /// <summary>
    /// 曜日を日本語表示へ変換する
    /// </summary>
    public static string GetDayLabel(DayOfWeek value) => DayOptions.First(x => x.Value == value).Label;

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        IsEnabled = settings.IsEnabled;
        DayOfWeek = settings.DayOfWeek;
        LocalTime = settings.LocalTime;
        LastScheduledExecutionDate = settings.LastScheduledExecutionDate;
        await LoadOperationalStateAsync(cancellationToken);
    }

    private async Task LoadOperationalStateAsync(CancellationToken cancellationToken)
    {
        RecentRuns = await operationsStore.GetRecentRunsAsync(30, cancellationToken);
        PendingRetries = await operationsStore.GetPendingRetriesAsync(cancellationToken);

        // 入力検証エラーで再表示する場合も最終実行日は設定ストアから取得し直す。
        var settings = await scheduleStore.GetAsync(cancellationToken);
        LastScheduledExecutionDate = settings.LastScheduledExecutionDate;
    }
}
