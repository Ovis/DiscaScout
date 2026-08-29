using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using DiscaScout.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["DiscaScout:DatabasePath"] ?? "data/discascout.db";
var imageCachePath = builder.Configuration["DiscaScout:ImageCachePath"] ?? "data/images";
var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrEmpty(databaseDirectory))
{
    Directory.CreateDirectory(databaseDirectory);
}
Directory.CreateDirectory(Path.GetFullPath(imageCachePath));

builder.Services.AddRazorPages();
builder.Services.AddDbContext<DiscaScoutDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

// 検索HTMLはCategory/Artist Catalogをまたいで完全直列化し、最低2秒間隔と10ページごとの追加休止を共有する。
builder.Services.AddSingleton<DiscasRequestThrottle>();
builder.Services.AddHttpClient<DiscasPageFetcher>();

// ジャケット画像は検索HTMLとは独立したBackgroundServiceで最大4並列取得する。
// 画像取得を検索処理の完了条件に含めず、一覧データを先に利用可能にする。
builder.Services.AddHttpClient("disc-image-cache");
builder.Services.AddScoped<DiscasSearchResultParser>();
builder.Services.AddScoped<DiscasCategoryCrawler>();
builder.Services.AddScoped<IDiscasCategoryCrawler>(sp => sp.GetRequiredService<DiscasCategoryCrawler>());
builder.Services.AddScoped<DiscasArtistCatalogCrawler>();
builder.Services.AddScoped<IDiscasArtistCatalogCrawler>(sp => sp.GetRequiredService<DiscasArtistCatalogCrawler>());
builder.Services.AddScoped<DiscasSnapshotApplier>();
builder.Services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
builder.Services.AddScoped<ArtistWatchService>();
builder.Services.AddScoped<ArtistCatalogStore>();
builder.Services.AddScoped(sp => new DiscImageCacheService(
    sp.GetRequiredService<DiscaScoutDbContext>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("disc-image-cache"),
    Path.GetFullPath(imageCachePath)));
builder.Services.AddScoped<ArtistCatalogCollectionService>();
builder.Services.AddScoped<ScrapeOperationsStore>();
builder.Services.AddScoped<IScrapeOperationsStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<IScrapeOperationsQueryStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>();
builder.Services.AddScoped<ManualWorkStore>();
builder.Services.AddScoped<DiscasScrapeService>();
builder.Services.AddScoped<ScrapeRunCoordinator>();
builder.Services.AddSingleton<ScrapeExecutionGate>();
builder.Services.AddSingleton<ManualWorkSignal>();
builder.Services.AddHostedService<ScrapeBackgroundService>();
builder.Services.AddHostedService<DiscImageCacheBackgroundService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<DiscaScoutDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.MapGet("/", () => Results.Redirect("/discs"));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/disc-image/{id:long}", async (long id, DiscaScoutDbContext dbContext, CancellationToken cancellationToken) =>
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
app.MapRazorPages();

await app.RunAsync();
