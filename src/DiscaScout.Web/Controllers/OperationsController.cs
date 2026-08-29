using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace DiscaScout.Web.Controllers;

/// <summary>
/// 定期取得設定、手動実行要求、実行履歴を管理する運用画面を提供する
/// </summary>
[Route("operations")]
public sealed class OperationsController(
    IScrapeScheduleStore scheduleStore,
    IScrapeOperationsQueryStore operationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal) : Controller
{
    /// <summary>現在の設定と運用状態を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await LoadAsync(null, cancellationToken));

    /// <summary>定期実行設定を保存する</summary>
    [HttpPost("schedule")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(bool isEnabled, DayOfWeek dayOfWeek, TimeOnly localTime, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(dayOfWeek)) ModelState.AddModelError(nameof(dayOfWeek), "曜日が不正です");
        if (!ModelState.IsValid)
        {
            return View("Index", await LoadOperationalStateAsync(isEnabled, dayOfWeek, localTime, null, cancellationToken));
        }

        await scheduleStore.UpdateAsync(isEnabled, dayOfWeek, localTime, cancellationToken);
        TempData[nameof(OperationsViewModel.StatusMessage)] = isEnabled
            ? $"定期取得を {OperationsViewModel.GetDayLabel(dayOfWeek)} {localTime:HH:mm} に設定しました"
            : "定期取得を無効にしました";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>UpcomingとNewの手動取得をBackgroundServiceへ登録する</summary>
    [HttpPost("run-now")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunNow(CancellationToken cancellationToken)
    {
        var enqueued = await manualWorkStore.TryEnqueueFullScrapeAsync(DateTime.UtcNow, cancellationToken);
        if (enqueued)
        {
            manualWorkSignal.Notify();
            TempData[nameof(OperationsViewModel.StatusMessage)] = "手動取得を受け付けました。バックグラウンドで実行します";
        }
        else
        {
            TempData[nameof(OperationsViewModel.StatusMessage)] = "通常取得系の手動処理は既に保留中または実行中です";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<OperationsViewModel> LoadAsync(string? statusMessage, CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        return await LoadOperationalStateAsync(
            settings.IsEnabled,
            settings.DayOfWeek,
            settings.LocalTime,
            statusMessage ?? TempData[nameof(OperationsViewModel.StatusMessage)] as string,
            cancellationToken);
    }

    private async Task<OperationsViewModel> LoadOperationalStateAsync(
        bool isEnabled,
        DayOfWeek dayOfWeek,
        TimeOnly localTime,
        string? statusMessage,
        CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        return new OperationsViewModel
        {
            IsEnabled = isEnabled,
            DayOfWeek = dayOfWeek,
            LocalTime = localTime,
            LastScheduledExecutionDate = settings.LastScheduledExecutionDate,
            RecentRuns = await operationsQueryStore.GetRecentRunsAsync(30, cancellationToken),
            PendingRetries = await operationsQueryStore.GetPendingRetriesAsync(cancellationToken),
            ActiveManualWork = await manualWorkStore.GetActiveAsync(cancellationToken),
            RecentManualWork = await manualWorkStore.GetRecentAsync(20, cancellationToken),
            StatusMessage = statusMessage
        };
    }
}
