import { parseRentalHistory } from "./parser.js";

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message.type !== "parse-html") return false;
    try {
        sendResponse({ ok: true, parsed: parseRentalHistory(message.html) });
    } catch (error) {
        sendResponse({ ok: false, error: error instanceof Error ? error.message : String(error) });
    }
    return false;
});
