using System.Text.Json;
using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace DiscaScout.Web.Controllers;

/// <summary>
/// 定期取得設定、手動実行要求、レンタル履歴インポート、実行履歴を管理する運用画面を提供する
/// </summary>
[Route("operations")]
public sealed class OperationsController(
    IScrapeScheduleStore scheduleStore,
    IScrapeOperationsQueryStore operationsQueryStore,
    ManualWorkStore manualWorkStore,
    ManualWorkSignal manualWorkSignal,
    DiscDetailMetadataService detailMetadataService,
    RentalHistoryImportService rentalHistoryImportService,
    DiscDetailFetchSignal detailFetchSignal) : Controller
{
    private static readonly JsonSerializerOptions RentalHistoryJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>現在の設定と運用状態を表示する</summary>
    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken) => View(await LoadAsync(null, null, cancellationToken));

    /// <summary>定期実行設定を保存する</summary>
    [HttpPost("schedule")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSchedule(bool isEnabled, DayOfWeek dayOfWeek, TimeOnly localTime, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(dayOfWeek)) ModelState.AddModelError(nameof(dayOfWeek), "曜日が不正です");
        if (!ModelState.IsValid)
        {
            return View("Index", await LoadOperationalStateAsync(isEnabled, dayOfWeek, localTime, null, null, cancellationToken));
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

    /// <summary>
    /// ログイン済みブラウザから抽出したCDレンタル履歴JSONを取り込む
    /// </summary>
    [HttpPost("rental-history-import")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportRentalHistory(string? rentalHistoryJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rentalHistoryJson))
        {
            ModelState.AddModelError(nameof(rentalHistoryJson), "インポートするJSONを入力してください");
            return View("Index", await LoadAsync(null, rentalHistoryJson, cancellationToken));
        }

        try
        {
            var entries = JsonSerializer.Deserialize<RentalHistoryImportEntry[]>(rentalHistoryJson, RentalHistoryJsonOptions);
            if (entries is null || entries.Length == 0)
            {
                ModelState.AddModelError(nameof(rentalHistoryJson), "インポート対象がありません");
                return View("Index", await LoadAsync(null, rentalHistoryJson, cancellationToken));
            }

            var result = await rentalHistoryImportService.ImportAsync(entries, cancellationToken);
            foreach (var discId in result.PriorityDiscIds)
            {
                // 履歴インポートの主目的は既レンタル判定だが、履歴だけに存在するCDもすぐ詳細情報を補完したい。
                // 既存の優先キューを再利用することで、DISCASへの共有Throttleと15秒間隔はそのまま維持する。
                detailFetchSignal.Request(discId);
            }

            TempData[nameof(OperationsViewModel.StatusMessage)] =
                $"レンタル履歴 {result.InputCount} 件を取り込みました。新規 {result.CreatedCount} 件、今回レンタル済みに変更 {result.MarkedRentedCount} 件、既にレンタル済み {result.AlreadyRentedCount} 件です";
            return RedirectToAction(nameof(Index));
        }
        catch (JsonException exception)
        {
            ModelState.AddModelError(nameof(rentalHistoryJson), $"JSONを解析できません: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(nameof(rentalHistoryJson), exception.Message);
        }

        return View("Index", await LoadAsync(null, rentalHistoryJson, cancellationToken));
    }

    private async Task<OperationsViewModel> LoadAsync(string? statusMessage, string? rentalHistoryJson, CancellationToken cancellationToken)
    {
        var settings = await scheduleStore.GetAsync(cancellationToken);
        return await LoadOperationalStateAsync(
            settings.IsEnabled,
            settings.DayOfWeek,
            settings.LocalTime,
            statusMessage ?? TempData[nameof(OperationsViewModel.StatusMessage)] as string,
            rentalHistoryJson,
            cancellationToken);
    }

    private async Task<OperationsViewModel> LoadOperationalStateAsync(
        bool isEnabled,
        DayOfWeek dayOfWeek,
        TimeOnly localTime,
        string? statusMessage,
        string? rentalHistoryJson,
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
            DetailFetchProgress = await detailMetadataService.GetProgressAsync(cancellationToken),
            RentalHistoryJson = rentalHistoryJson,
            StatusMessage = statusMessage
        };
    }
}
