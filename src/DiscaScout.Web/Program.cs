using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using DiscaScout.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["DiscaScout:DatabasePath"] ?? "data/discascout.db";
var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrEmpty(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}

builder.Services.AddRazorPages();
builder.Services.AddDbContext<DiscaScoutDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

// DISCASへの全HTTPアクセスで同じ排他・2秒間隔を共有する。
// Crawler単位でFetcherが別インスタンスになっても相手サーバーへの並列アクセスを発生させない。
builder.Services.AddSingleton<DiscasRequestThrottle>();
builder.Services.AddHttpClient<DiscasPageFetcher>();
builder.Services.AddScoped<DiscasSearchResultParser>();
builder.Services.AddScoped<DiscasCategoryCrawler>();
builder.Services.AddScoped<IDiscasCategoryCrawler>(sp => sp.GetRequiredService<DiscasCategoryCrawler>());
builder.Services.AddScoped<DiscasArtistCatalogCrawler>();
builder.Services.AddScoped<IDiscasArtistCatalogCrawler>(sp => sp.GetRequiredService<DiscasArtistCatalogCrawler>());
builder.Services.AddScoped<DiscasSnapshotApplier>();
builder.Services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
builder.Services.AddScoped<ArtistWatchService>();
builder.Services.AddScoped<ArtistCatalogStore>();
builder.Services.AddScoped<ArtistCatalogCollectionService>();
builder.Services.AddScoped<ScrapeOperationsStore>();
builder.Services.AddScoped<IScrapeOperationsStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<IScrapeOperationsQueryStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>();
builder.Services.AddScoped<DiscasScrapeService>();
builder.Services.AddScoped<ScrapeRunCoordinator>();
builder.Services.AddSingleton<ScrapeExecutionGate>();
builder.Services.AddHostedService<ScrapeBackgroundService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DiscaScoutDbContext>();

    // 永続DBを破棄せず今後のスキーマ変更を適用できるよう、起動時はMigrationを前進適用する。
    // 単一インスタンス運用を前提としているため、同じSQLite DBへ複数ホストが同時Migrationする構成は採らない。
    await dbContext.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Redirect("/operations"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapRazorPages();

await app.RunAsync();
