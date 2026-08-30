using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using DiscaScout.Web;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var databasePath = builder.Configuration["DiscaScout:DatabasePath"] ?? "data/discascout.db";
var imageCachePath = builder.Configuration["DiscaScout:ImageCachePath"] ?? "data/images";
var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrEmpty(databaseDirectory)) Directory.CreateDirectory(databaseDirectory);
Directory.CreateDirectory(Path.GetFullPath(imageCachePath));

// 画面処理はControllerへ集約し、RazorはViewとしてのみ使用する。
// Razor Pagesとの併存を残すとルーティングとフォーム記法が二重化するため、MVCだけを登録する。
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<DiscaScoutDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddSingleton<DiscasRequestThrottle>();
builder.Services.AddHttpClient<DiscasPageFetcher>();
// Discordは取得処理とは独立した監視経路として扱い、設定自体は運用画面からSQLiteへ保存する。
builder.Services.AddHttpClient("discord-webhook");
builder.Services.AddScoped<DiscordNotificationSettingsStore>();
builder.Services.AddScoped<DiscordNotificationService>();
builder.Services.AddHttpClient("disc-image-cache");
builder.Services.AddScoped<DiscasSearchResultParser>(); builder.Services.AddScoped<DiscasDiscDetailParser>();
builder.Services.AddScoped<DiscasCategoryCrawler>(); builder.Services.AddScoped<IDiscasCategoryCrawler>(sp => sp.GetRequiredService<DiscasCategoryCrawler>());
builder.Services.AddScoped<DiscasArtistCatalogCrawler>(); builder.Services.AddScoped<IDiscasArtistCatalogCrawler>(sp => sp.GetRequiredService<DiscasArtistCatalogCrawler>());
builder.Services.AddScoped<DiscasSnapshotApplier>(); builder.Services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
builder.Services.AddScoped<ArtistWatchService>(); builder.Services.AddScoped<ArtistCatalogStore>();
builder.Services.AddScoped(sp => new DiscImageCacheService(sp.GetRequiredService<DiscaScoutDbContext>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("disc-image-cache"), Path.GetFullPath(imageCachePath)));
builder.Services.AddScoped<ArtistCatalogCollectionService>(); builder.Services.AddScoped<DiscDetailMetadataService>(); builder.Services.AddScoped<RentalHistoryImportService>();
builder.Services.AddScoped<ScrapeOperationsStore>(); builder.Services.AddScoped<IScrapeOperationsStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>()); builder.Services.AddScoped<IScrapeOperationsQueryStore>(sp => sp.GetRequiredService<ScrapeOperationsStore>());
builder.Services.AddScoped<ScrapeGuardStore>(); builder.Services.AddScoped<IScrapeGuardStore>(sp => sp.GetRequiredService<ScrapeGuardStore>());
builder.Services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>(); builder.Services.AddScoped<ManualWorkStore>(); builder.Services.AddScoped<DiscasScrapeService>(); builder.Services.AddScoped<ScrapeRunCoordinator>();
builder.Services.AddSingleton<ScrapeExecutionGate>(); builder.Services.AddSingleton<ManualWorkSignal>(); builder.Services.AddSingleton<DiscDetailFetchSignal>();
builder.Services.AddHostedService<ScrapeBackgroundService>(); builder.Services.AddHostedService<DiscImageCacheBackgroundService>(); builder.Services.AddHostedService<DiscDetailMetadataBackgroundService>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DiscaScoutDbContext>().Database.MigrateAsync();

    // #38で詳細ページのナビゲーション文字列をジャンルとして保存した可能性があるため、
    // ホスト起動時に明らかな汚染データだけを未取得へ戻してバックグラウンド再取得へ回す。
    await scope.ServiceProvider.GetRequiredService<DiscDetailMetadataService>().RepairCorruptedImportedGenresAsync();
}
app.MapGet("/", () => Results.Redirect("/discs")); app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/disc-image/{id:long}", async (long id, DiscaScoutDbContext dbContext, CancellationToken cancellationToken) =>
{
    var imagePath = await dbContext.Discs.AsNoTracking().Where(x => x.Id == id).Select(x => x.ImagePath).SingleOrDefaultAsync(cancellationToken);
    if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return Results.NotFound();
    var contentType = Path.GetExtension(imagePath).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
    return Results.File(imagePath, contentType, enableRangeProcessing: false);
});
app.MapControllers();
await app.RunAsync();
