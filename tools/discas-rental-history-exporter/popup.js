const elements = Object.fromEntries(["page-warning","status","pages","rows","cd-rows","unique-cds","detail","start","resume","cancel","result","result-summary","copy","download","debug","debug-download"].map(id => [id, document.getElementById(id)]));
let snapshot = null;

await refresh();
setInterval(refresh, 1000);

elements.start.addEventListener("click", () => run("start"));
elements.resume.addEventListener("click", () => run("resume"));
elements.cancel.addEventListener("click", () => run("cancel"));
elements.copy.addEventListener("click", async () => navigator.clipboard.writeText(JSON.stringify(snapshot.result.records, null, 2)));
elements.download.addEventListener("click", () => downloadJson(snapshot.result.records, "discas-rental-history.json"));
elements["debug-download"].addEventListener("click", () => downloadJson(buildDebug(snapshot.state), "discas-rental-history-diagnostic.json"));

async function refresh() {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    // DISCASのレンタル履歴はwish配下にある。別のrentalLog.doを誤認しないようパスまで確認する。
    const onHistoryPage = /^https:\/\/www\.discas\.net\/netdvd\/wish\/rentalLog\.do(?:[?#]|$)/.test(tab?.url ?? "");
    elements["page-warning"].hidden = onHistoryPage;
    snapshot = await chrome.runtime.sendMessage({ type: "get-state" });
    render(snapshot.state, snapshot.result, onHistoryPage);
}

async function run(type) {
    await chrome.runtime.sendMessage({ type });
    await refresh();
}

function render(state, result, onHistoryPage) {
    const active = state && ["running", "retrying"].includes(state.status);
    elements.start.disabled = !onHistoryPage || active;
    elements.start.hidden = state?.status === "cancelled";
    elements.resume.hidden = state?.status !== "cancelled" && state?.status !== "failed";
    elements.resume.disabled = !onHistoryPage;
    elements.cancel.hidden = !active;
    elements.status.textContent = formatStatus(state);
    elements.pages.textContent = state ? `${state.currentPage} / ${state.totalPages ?? "?"}` : "-";
    elements.rows.textContent = state ? `${state.parsedRows} / ${state.expectedTotalRows ?? "?"}` : "-";
    elements["cd-rows"].textContent = state?.cdRows ?? "-";
    elements["unique-cds"].textContent = state?.records?.length ?? "-";
    elements.detail.textContent = state?.status === "retrying" ? `ページ ${state.retryPage} をRetry中 (${state.retryAttempt}/3)\n${state.error ?? ""}` : (state?.error ?? "");

    elements.result.hidden = !result;
    if (result) elements["result-summary"].textContent = `${result.stats.expectedRows}件を検証 / CD履歴 ${result.stats.cdRows}件 / 重複排除後 ${result.stats.uniqueCds}作品 / 重複 ${result.stats.duplicateCdRows}件 / メタデータ不一致 ${result.metadataConflicts.length}作品`;
    elements.debug.hidden = state?.status !== "invalid";
}

function formatStatus(state) {
    if (!state) return "未実行";
    return ({ running: "取得中", retrying: "Retry中", cancelled: "中止", completed: "取得完了", invalid: "完全性チェック失敗", failed: "取得失敗" })[state.status] ?? state.status;
}

function buildDebug(state) {
    return {
        status: state.status,
        expectedTotalRows: state.expectedTotalRows,
        parsedRows: state.parsedRows,
        totalPages: state.totalPages,
        pageStats: state.pageStats,
        validationErrors: state.validationErrors ?? [],
        parseErrors: state.parseErrors,
        metadataConflicts: state.metadataConflicts,
        records: state.records
    };
}

function downloadJson(value, filename) {
    const blob = new Blob([JSON.stringify(value, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = filename;
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
