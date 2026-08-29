using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["DiscaScout:DatabasePath"] ?? "data/discascout.db";
var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrEmpty(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}

builder.Services.AddDbContext<DiscaScoutDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddHttpClient<DiscasPageFetcher>();
builder.Services.AddScoped<DiscasSearchResultParser>();
builder.Services.AddScoped<DiscasCategoryCrawler>();
builder.Services.AddScoped<IDiscasCategoryCrawler>(sp => sp.GetRequiredService<DiscasCategoryCrawler>());
builder.Services.AddScoped<DiscasSnapshotApplier>();
builder.Services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
builder.Services.AddScoped<IScrapeOperationsStore, ScrapeOperationsStore>();
builder.Services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>();
builder.Services.AddScoped<DiscasScrapeService>();
builder.Services.AddScoped<ScrapeRunCoordinator>();
builder.Services.AddSingleton<ScrapeExecutionGate>();
builder.Services.AddHostedService<ScrapeBackgroundService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DiscaScoutDbContext>();

    // まだ初回リリース前でMigration運用を開始していないため、開発中のホスト起動ではDBを自動作成する。
    // 永続DBを正式運用する前にMigrationへ切り替え、既存DBを破棄せずスキーマ更新できる状態にする。
    await dbContext.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

await app.RunAsync();
