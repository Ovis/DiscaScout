using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DiscaScout.Web.Pages;

/// <summary>
/// 定期取得設定、手動実行要求、実行履歴を管理する運用画面
/// </summary>
public sealed class OperationsModel(
    IScrapeScheduleStore scheduleStore,
    IScrapeOperationsQueryStore operationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : PageModel
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

    /// <summary>直近のスクレイピング実行履歴</summary>
    public IReadOnlyList<ScrapeRun> RecentRuns { get; private set; } = [];

    /// <summary>現在保留中のRetry</summary>
    public IReadOnlyList<ScrapeRetry> PendingRetries { get; private set; } = [];

    /// <summary>現在保留または実行中の手動処理</summary>
    public IReadOnlyList<ManualWorkItem> ActiveManualWork { get; private set; } = [];

    /// <summary>直近の手動処理要求履歴</summary>
    public IReadOnlyList<ManualWorkItem> RecentManualWork { get; private set; } = [];

    /// <summary>通常の手動取得が既に保留または実行中か</summary>
    public bool IsFullScrapeActive => ActiveManualWork.Any(x => x.Type == ManualWorkType.FullScrape);

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
            ? $"定期取得を {GetDayLabel(DayOfWeek)} {LocalTime:HH:mm} に設定しました"
            : "定期取得を無効にしました";
        return RedirectToPage();
    }

    /// <summary>
    /// UpcomingとNewの手動取得をBackgroundServiceへ登録する
    /// </summary>
    public async Task<IActionResult> OnPostRunNowAsync(CancellationToken cancellationToken)
    {
        var enqueued = await manualWorkStore.TryEnqueueFullScrapeAsync(DateTime.UtcNow, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
            StatusMessage = "手動取得を受け付けました。バックグラウンドで実行します";
        }
        else
        {
            StatusMessage = "手動取得は既に保留中または実行中です";
        }

        return RedirectToPage();
    }

    /// <summary>
    /// UTCで保存した履歴時刻を運用画面用の日本時間へ変換する
    /// </summary>
    /// <param name="value">UTCとして保存されている時刻</param>
    /// <returns>日本時間</returns>
    public static DateTime ToJapanTime(DateTime value)
    {
        // SQLiteから読み出したDateTimeのKindに依存せず、永続値はUTCというモデル上の前提をここで明示する。
        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, JapanTimeZone);
    }

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
        RecentRuns = await operationsQueryStore.GetRecentRunsAsync(30, cancellationToken);
        PendingRetries = await operationsQueryStore.GetPendingRetriesAsync(cancellationToken);
        ActiveManualWork = await manualWorkStore.GetActiveAsync(cancellationToken);
        RecentManualWork = await manualWorkStore.GetRecentAsync(20, cancellationToken);

        // 入力検証エラーで再表示する場合も最終実行日は設定ストアから取得し直す。
        var settings = await scheduleStore.GetAsync(cancellationToken);
        LastScheduledExecutionDate = settings.LastScheduledExecutionDate;
    }
}
