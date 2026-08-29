# DISCAS Live Crawl Verification — 2026-08-29

## Purpose

This note records the live verification performed after implementing the full-category crawler. It supplements `discas-scraping.md` with concrete end-to-end results from the current DISCAS CD search pages.

## New

Command:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- --crawl new
```

Result:

- Category: `New`
- Reported total: `1,528`
- Pages: `39`
- Parsed products: `1,528`
- Page-level parsed `titleID` sequences matched DISCAS hidden `titleId` values
- Final parsed count matched the reported total

The probe saved every fetched page before parsing as:

```text
artifacts/probe/new-page-001.html
...
artifacts/probe/new-page-039.html
```

### Alternate artist markup

The first full-genre crawl exposed a product that did not use the normal second `.cd-search-product-title` artist heading.

Observed product:

```text
titleID: 7635390756
Title: 【MAXI】Which Way is Love?(マキシシングル)
Artist: トレンドジ―
```

The artist is present in the HTML using:

```html
<h3 class="cd-search-artist-not-available">トレンドジ―</h3>
```

Parser policy is therefore:

1. Use the second `.card-body-searchCd .cd-search-product-title` heading when present.
2. Otherwise use `.cd-search-artist-not-available`.
3. If neither contains a non-empty artist value, treat the product as a parse failure rather than inventing metadata.

`ScrapedDisc.Artist` remains non-nullable.

## Upcoming

Command:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- --crawl upcoming
```

Result:

- Category: `Upcoming`
- Reported total: `821`
- Pages: `21`
- Parsed products: `821`
- Page-level parsed `titleID` sequences matched DISCAS hidden `titleId` values
- Final parsed count matched the reported total

The probe saved every fetched page before parsing as:

```text
artifacts/probe/upcoming-page-001.html
...
artifacts/probe/upcoming-page-021.html
```

## Conclusion

As of 2026-08-29, the scraper can obtain complete `New` and `Upcoming` CD category snapshots across all genres using `HttpClient` and AngleSharp without browser automation.

The production persistence layer may rely on a category snapshot only after the crawler has completed all pages and passed ID/count validation. A partial or inconsistent crawl must not be persisted as the current category state.
