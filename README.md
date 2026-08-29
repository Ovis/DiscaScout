# DiscaScout

DiscaScout is a local web application for tracking CD releases and rental status using TSUTAYA DISCAS search data.

Development starts from the backend: scraping, parsing, persistence, change detection, and scheduling are implemented and verified before UI refinement.

## Current milestone

The first milestone is a scraper proof of concept. It intentionally does not implement the application UI or database yet.

The probe verifies that a DISCAS CD search page can be fetched with `HttpClient`, saves the raw HTML as a local artifact, parses it with AngleSharp, and reports candidate product links and image URLs. The captured HTML will be used to determine stable selectors before implementing the production search-result parser.

## Requirements

- .NET 10 SDK

## Run the scraper probe

```powershell
dotnet run --project src/DiscaScout.ScraperProbe
```

The default URL targets the first page of the DISCAS CD new-release search. A different search URL can be supplied as the first argument:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- "https://movie-tsutaya.tsite.jp/netdvd/cd/searchCd.do?..."
```

Raw HTML is written under `artifacts/probe/`, which is ignored by Git.

## Tests

```powershell
dotnet test DiscaScout.slnx
```

## Next steps

1. Run the probe against the current DISCAS search pages.
2. Inspect the captured HTML for product identity, title, artist, image, rental-start date, total-result count, sort state, and paging structure.
3. Preserve representative HTML as test fixtures after removing irrelevant or volatile content where appropriate.
4. Implement a deterministic search-result parser and pagination model.
5. Only after the scraper contract is established, introduce persistence and change detection.
