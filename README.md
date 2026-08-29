# DiscaScout

DiscaScout is a local web application for tracking CD releases and rental status using TSUTAYA DISCAS search data.

Development starts from the backend: scraping, parsing, persistence, change detection, and scheduling are implemented and verified before UI refinement.

## Current milestone

The first milestone is a scraper proof of concept. It intentionally does not implement the application UI or database yet.

The current probe verifies that DISCAS CD search pages can be fetched with `HttpClient`, decoded from Windows-31J, parsed with AngleSharp, and converted into structured product data. The parser validates the product IDs against DISCAS's hidden `titleId` list, and the category crawler can retrieve every page before returning a complete snapshot.

## Requirements

- .NET 10 SDK

## Run the scraper probe

The default probe fetches the first page of the all-genre DISCAS CD new-release search, sorted by newest rental start date.

```powershell
dotnet run --project src/DiscaScout.ScraperProbe
```

To probe the upcoming-release category instead:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- probe upcoming
```

Raw HTML is written under `artifacts/probe/`, which is ignored by Git.

### Crawl an entire category

To fetch and validate every page in a category:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- --crawl new
```

or:

```powershell
dotnet run --project src/DiscaScout.ScraperProbe -- --crawl upcoming
```

The full crawl does not persist anything. It succeeds only when all pages can be fetched and parsed, each page's parsed product IDs match DISCAS's hidden `titleId` list, the total count is consistent across pages, and the final parsed count matches the reported total.

## Tests

```powershell
dotnet test DiscaScout.slnx
```

## Next steps

1. Verify the all-genre new and upcoming searches against the current live DISCAS pages.
2. Confirm that complete category crawls pass the page-level and category-level integrity checks.
3. Introduce the domain model and persistence only after the scraper contract is stable.
4. Add change detection and source lifecycle behavior on top of complete category snapshots.
