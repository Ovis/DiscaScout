export function parseRentalHistory(html) {
    const document = new DOMParser().parseFromString(html, "text/html");
    const bodyText = document.body?.textContent ?? "";
    const totalMatch = bodyText.match(/全\s*([\d,]+)\s*件/);
    const totalCount = totalMatch ? Number(totalMatch[1].replaceAll(",", "")) : null;

    const pageNumbers = [...document.querySelectorAll("a[href*='pageNo=']")]
        .map(a => Number(new URL(a.href, "https://www.discas.net").searchParams.get("pageNo")))
        .filter(Number.isFinite);
    const totalPages = pageNumbers.length ? Math.max(...pageNumbers) : (totalCount === null ? null : Math.max(1, Math.ceil(totalCount / 20)));

    // 商品詳細リンクを履歴行のアンカーとして扱う。CD以外の行も完全性検証では1件として数える必要がある。
    const productLinks = [...document.querySelectorAll("a[href*='/netdvd/'][href*='goodsDetail.do'][href*='titleID=']")];
    const rows = [];
    const seenElements = new Set();

    for (const link of productLinks) {
        const container = findHistoryContainer(link);
        if (!container || seenElements.has(container)) continue;
        seenElements.add(container);

        const titleId = new URL(link.href, "https://www.discas.net").searchParams.get("titleID");
        const cdIcon = container.querySelector("img[src*='ic_cat_cd_s.png']");
        const title = cleanText(link.textContent);
        const artist = extractArtist(container, link);
        const errors = [];
        if (!titleId) errors.push("titleIDを取得できませんでした");
        if (!title) errors.push("タイトルを取得できませんでした");
        if (cdIcon && !artist) errors.push("アーティストを取得できませんでした");

        rows.push({ titleId, title, artist, isCd: Boolean(cdIcon), errors });
    }

    return { totalCount, totalPages, rows };
}

function findHistoryContainer(link) {
    let element = link;
    for (let depth = 0; element && depth < 8; depth++, element = element.parentElement) {
        if (element.querySelector?.("img[src*='ic_cat_']") && element.querySelectorAll?.("a[href*='titleID=']").length === 1) return element;
    }
    return link.closest("li, tr, article, section, div");
}

function extractArtist(container, titleLink) {
    const links = [...container.querySelectorAll("a")].filter(x => x !== titleLink);
    const candidate = links.find(x => !x.href.includes("goodsDetail.do") && cleanText(x.textContent));
    return cleanText(candidate?.textContent);
}

function cleanText(value) {
    return (value ?? "").replace(/\s+/g, " ").trim();
}
