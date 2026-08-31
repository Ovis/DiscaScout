using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using DiscaScout.Web;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["DiscaScout:DatabasePath"] ?? "data/discascout.db";
var imageCachePath = builder.Configuration["DiscaScout:ImageCachePath"] ?? "data/images";
var logPath = builder.Configuration["DiscaScout:LogPath"] ?? "data/logs/discascout-.log";

var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
var logDirectory = Path.GetDirectoryName(Path.GetFullPath(logPath));

if (!string.IsNullOrEmpty(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}

if (!string.IsNullOrEmpty(logDirectory))
{
    Directory.CreateDirectory(logDirectory);
}

Directory.CreateDirectory(Path.GetFullPath(imageCachePath));

// 標準のコンソールログはそのまま残し、永続化が必要な運用ログだけを追加のSerilog Providerでdata配下へ複製する。
// 日次ローテーションに加えて31日を超えたファイルを削除し、長期運用でログが無制限に増えないようにする。
var fileLogger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.File(
        Path.GetFullPath(logPath),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        retainedFileTimeLimit: TimeSpan.FromDays(31))
    .CreateLogger();

builder.Logging.AddSerilog(fileLogger, dispose: true);

builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<DiscaScoutDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

builder.Services.AddSingleton<DiscasRequestThrottle>();
builder.Services.AddHttpClient<DiscasPageFetcher>();
builder.Services.AddHttpClient("discord-webhook");
builder.Services.AddHttpClient("disc-image-cache");

builder.Services.AddScoped<DiscordNotificationSettingsStore>();
builder.Services.AddScoped<DiscordNotificationService>();
builder.Services.AddScoped<DiscasSearchResultParser>();
builder.Services.AddScoped<DiscasDiscDetailParser>();
builder.Services.AddScoped<DiscasGenreMasterParser>();
builder.Services.AddScoped<GenreResolver>();
builder.Services.AddScoped<GenreMasterService>();
builder.Services.AddScoped<DiscasCategoryCrawler>();
builder.Services.AddScoped<IDiscasCategoryCrawler>(sp =>
    sp.GetRequiredService<DiscasCategoryCrawler>());
builder.Services.AddScoped<DiscasArtistCatalogCrawler>();
builder.Services.AddScoped<IDiscasArtistCatalogCrawler>(sp =>
    sp.GetRequiredService<DiscasArtistCatalogCrawler>());
builder.Services.AddScoped<DiscasSnapshotApplier>();
builder.Services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
builder.Services.AddScoped<ArtistWatchService>();
builder.Services.AddScoped<ArtistCatalogStore>();
builder.Services.AddScoped(sp => new DiscImageCacheService(
    sp.GetRequiredService<DiscaScoutDbContext>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("disc-image-cache"),
    Path.GetFullPath(imageCachePath)));
builder.Services.AddScoped<ArtistCatalogCollectionService>();
builder.Services.AddScoped<DiscDetailMetadataService>();
builder.Services.AddScoped<RentalHistoryImportService>();
builder.Services.AddScoped<ScrapeOperationsStore>();
builder.Services.AddScoped<IScrapeOperationsStore>(sp =>
    sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<IScrapeOperationsQueryStore>(sp =>
    sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<ScrapeGuardStore>();
builder.Services.AddScoped<IScrapeGuardStore>(sp =>
    sp.GetRequiredService<ScrapeGuardStore>());
builder.Services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>();
builder.Services.AddScoped<ManualWorkStore>();
builder.Services.AddScoped<DiscasScrapeService>();
builder.Services.AddScoped<ScrapeRunCoordinator>();

builder.Services.AddSingleton<ScrapeExecutionGate>();
builder.Services.AddSingleton<ManualWorkSignal>();
builder.Services.AddSingleton<DiscDetailFetchSignal>();

builder.Services.AddHostedService<ScrapeBackgroundService>();
builder.Services.AddHostedService<DiscImageCacheBackgroundService>();
builder.Services.AddHostedService<DiscDetailMetadataBackgroundService>();

var app = builder.Build();

// 起動時にMigrationを適用し、永続DBを現在のアプリケーションモデルへ揃える。
await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DiscaScoutDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Redirect("/discs"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet(
    "/disc-image/{id:long}",
    async (long id, DiscaScoutDbContext dbContext, CancellationToken cancellationToken) =>
    {
        var imagePath = await dbContext.Discs
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => x.ImagePath)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return Results.NotFound();
        }

        var contentType = Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/jpeg"
        };

        return Results.File(imagePath, contentType, enableRangeProcessing: false);
    });

app.MapControllers();

await app.RunAsync();
