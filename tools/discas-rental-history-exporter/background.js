const STATE_KEY = "rentalHistoryExporterState";
const RESULT_KEY = "rentalHistoryExporterLastSuccessfulResult";
const HISTORY_URL = "https://www.discas.net/netdvd/dvd/rentalLog.do";
const ALARM_NAME = "discas-rental-history-next-page";

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    handleMessage(message).then(sendResponse).catch(error => sendResponse({ ok: false, error: error.message }));
    return true;
});

chrome.alarms.onAlarm.addListener(alarm => {
    if (alarm.name === ALARM_NAME) processNextPage().catch(error => failJob(error.message));
});

chrome.runtime.onStartup.addListener(() => resumeInterruptedJob());
chrome.runtime.onInstalled.addListener(() => resumeInterruptedJob());

async function handleMessage(message) {
    switch (message.type) {
        case "get-state": return { ok: true, state: await getState(), result: (await chrome.storage.local.get(RESULT_KEY))[RESULT_KEY] ?? null };
        case "start": await startJob(false); return { ok: true };
        case "resume": await startJob(true); return { ok: true };
        case "cancel": await requestCancel(); return { ok: true };
        default: throw new Error("未対応の操作です。");
    }
}

async function startJob(isResume) {
    const previous = await getState();
    const state = createInitialState(isResume ? previous?.records ?? [] : []);
    await saveState(state);
    await scheduleNext(100);
}

function createInitialState(existingRecords) {
    return {
        status: "running", currentPage: 0, totalPages: null, expectedTotalRows: null,
        parsedRows: 0, cdRows: 0, records: existingRecords, pageStats: [], parseErrors: [], metadataConflicts: [],
        retryPage: null, retryAttempt: 0, cancelRequested: false, startedAt: new Date().toISOString(), error: null
    };
}

async function processNextPage() {
    const state = await getState();
    if (!state || !["running", "retrying"].includes(state.status)) return;
    if (state.cancelRequested) { state.status = "cancelled"; await saveState(state); return; }

    const page = state.currentPage + 1;
    try {
        const parsed = await fetchAndParse(page);
        if (page === 1) {
            if (parsed.totalCount === null || parsed.totalPages === null) throw new Error("履歴の総件数または総ページ数を取得できませんでした。ログイン状態とページ構造を確認してください。");
            state.expectedTotalRows = parsed.totalCount;
            state.totalPages = parsed.totalPages;
        }
        applyPage(state, page, parsed);
        state.currentPage = page;
        state.status = "running";
        state.retryPage = null;
        state.retryAttempt = 0;
        await saveState(state);

        if (page >= state.totalPages) { await completeJob(state); return; }
        if (state.cancelRequested) { state.status = "cancelled"; await saveState(state); return; }
        await scheduleNext(calculateNormalDelay(page));
    } catch (error) {
        await handlePageFailure(state, page, error);
    }
}

async function fetchAndParse(page) {
    // DOMParserはService Workerに存在しないため、解析専用のoffscreen documentへHTMLを渡す。
    await ensureOffscreenDocument();
    const url = `${HISTORY_URL}?pageNo=${page}&pT=0`;
    const response = await fetch(url, { credentials: "include", cache: "no-store", redirect: "follow" });
    if (!response.ok) throw new Error(`ページ${page}の取得に失敗しました (HTTP ${response.status})`);
    if (!response.url.includes("rentalLog.do")) throw new Error("レンタル履歴以外へリダイレクトされました。DISCASへ再ログインしてください。");
    const html = await response.text();
    const result = await chrome.runtime.sendMessage({ type: "parse-html", html });
    if (!result?.ok) throw new Error(result?.error ?? "HTMLを解析できませんでした。");
    return result.parsed;
}

function applyPage(state, page, parsed) {
    state.parsedRows += parsed.rows.length;
    const cdRows = parsed.rows.filter(x => x.isCd);
    state.cdRows += cdRows.length;
    state.pageStats.push({ page, rowCount: parsed.rows.length, cdCount: cdRows.length, parseErrorCount: parsed.rows.filter(x => x.errors.length).length });

    for (const [index, row] of parsed.rows.entries()) {
        if (row.errors.length) state.parseErrors.push({ page, row: index + 1, titleId: row.titleId, errors: row.errors });
        if (!row.isCd || !row.titleId || !row.title || !row.artist) continue;
        const existing = state.records.find(x => x.titleId === row.titleId);
        if (!existing) {
            state.records.push({ titleId: row.titleId, title: row.title, artist: row.artist });
        } else if (existing.title !== row.title || existing.artist !== row.artist) {
            state.metadataConflicts.push({ titleId: row.titleId, selected: existing, found: { title: row.title, artist: row.artist }, page });
        }
    }
}

async function completeJob(state) {
    const validationErrors = [];
    if (state.parsedRows !== state.expectedTotalRows) validationErrors.push(`履歴行数が一致しません。期待 ${state.expectedTotalRows} 件 / 解析 ${state.parsedRows} 件`);
    if (state.parseErrors.length) validationErrors.push(`解析不能な履歴行が ${state.parseErrors.length} 件あります。`);
    state.completedAt = new Date().toISOString();
    state.validationErrors = validationErrors;
    state.status = validationErrors.length ? "invalid" : "completed";
    await saveState(state);

    if (!validationErrors.length) {
        await chrome.storage.local.set({ [RESULT_KEY]: { completedAt: state.completedAt, records: state.records, metadataConflicts: state.metadataConflicts, stats: buildStats(state) } });
    }
}

async function handlePageFailure(state, page, error) {
    const attempt = state.retryPage === page ? state.retryAttempt + 1 : 1;
    if (attempt > 3) { await failJob(`ページ${page}を3回Retryしましたが取得できませんでした: ${error.message}`); return; }
    state.status = "retrying";
    state.retryPage = page;
    state.retryAttempt = attempt;
    state.error = error.message;
    await saveState(state);
    await scheduleNext([10000, 30000, 60000][attempt - 1]);
}

async function requestCancel() {
    const state = await getState();
    if (!state || !["running", "retrying"].includes(state.status)) return;
    state.cancelRequested = true;
    await saveState(state);
}

async function failJob(message) {
    const state = await getState();
    if (!state) return;
    state.status = "failed";
    state.error = message;
    await saveState(state);
}

function buildStats(state) {
    return { expectedRows: state.expectedTotalRows, parsedRows: state.parsedRows, cdRows: state.cdRows, uniqueCds: state.records.length, duplicateCdRows: state.cdRows - state.records.length, totalPages: state.totalPages };
}

function calculateNormalDelay(page) {
    return page % 10 === 0 ? 2000 + Math.floor(Math.random() * 15001) + 5000 : 2000;
}

async function scheduleNext(delayMs) {
    await chrome.alarms.clear(ALARM_NAME);
    chrome.alarms.create(ALARM_NAME, { when: Date.now() + delayMs });
}

async function ensureOffscreenDocument() {
    if (await chrome.offscreen.hasDocument()) return;
    await chrome.offscreen.createDocument({ url: "offscreen.html", reasons: ["DOM_PARSER"], justification: "DISCASレンタル履歴HTMLをDOMとして安全に解析するため" });
}

async function resumeInterruptedJob() {
    const state = await getState();
    if (state && ["running", "retrying"].includes(state.status)) await scheduleNext(1000);
}

async function getState() { return (await chrome.storage.local.get(STATE_KEY))[STATE_KEY] ?? null; }
async function saveState(state) { await chrome.storage.local.set({ [STATE_KEY]: state }); }
