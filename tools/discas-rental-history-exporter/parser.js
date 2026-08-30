export function parseRentalHistory(html) {
    const document = new DOMParser().parseFromString(html, "text/html");

    // 「これまでにご利用されたDVD/CD」の件数表示だけを完全性検証の基準にする。
    // 現在レンタル中の作品はこの総件数に含まれないため、別配列として返して1ページ目だけ加算する。
    const historySummary = document.querySelector(".dvd-cd-rented .sortView p")?.textContent ?? "";
    const totalMatch = historySummary.match(/全\s*([\d,]+)\s*件/);
    const historyTotalCount = totalMatch ? Number(totalMatch[1].replaceAll(",", "")) : null;

    const pageNumbers = [...document.querySelectorAll(".dvd-cd-rented .sortView a[href*='pageNo=']")]
        .map(a => Number(new URL(a.href, "https://www.discas.net/netdvd/wish/").searchParams.get("pageNo")))
        .filter(Number.isFinite);
    const totalPages = pageNumbers.length
        ? Math.max(...pageNumbers)
        : (historyTotalCount === null ? null : Math.max(1, Math.ceil(historyTotalCount / 20)));

    // PC向けDOMとモバイル向けDOMが同じHTML内に重複して存在するため、
    // 履歴本体はPC側の明示的な行クラスだけを対象にして二重計上を防ぐ。
    const historyRows = [...document.querySelectorAll("tr.row-data-used-dvd")].map(parseRow);

    // 現在レンタル中の一覧もPC側テーブルだけを対象にする。
    // 見出し行には商品詳細リンクがないため、商品リンクを持つtrだけを抽出する。
    const currentRows = [...document.querySelectorAll(".dvd-cd-rented.current-rented table tbody tr")]
        .filter(row => row.querySelector("a[href*='goodsDetail.do'][href*='titleID=']"))
        .map(parseRow);

    return { historyTotalCount, totalPages, historyRows, currentRows };
}

function parseRow(container) {
    const titleLink = container.querySelector(".wishlistTxt01 a[href*='goodsDetail.do'][href*='titleID=']")
        ?? container.querySelector("a[href*='goodsDetail.do'][href*='titleID=']");
    const cdIcon = container.querySelector("img[src*='ic_cat_cd_s.png']");
    const titleId = titleLink
        ? new URL(titleLink.href, "https://www.discas.net").searchParams.get("titleID")
        : null;
    const title = cleanText(titleLink?.textContent);
    const artistLink = container.querySelector(".wishlistTxt01 a[href*='/netdvd/cd/searchCd.do?a=']")
        ?? container.querySelector("a[href*='/netdvd/cd/searchCd.do?a=']");
    const artist = cleanText(artistLink?.textContent);
    const errors = [];

    if (!titleId) errors.push("titleIDを取得できませんでした");
    if (!title) errors.push("タイトルを取得できませんでした");
    if (cdIcon && !artist) errors.push("アーティストを取得できませんでした");

    return { titleId, title, artist, isCd: Boolean(cdIcon), errors };
}

function cleanText(value) {
    return (value ?? "").replace(/\s+/g, " ").trim();
}
