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
// ファイル側のログレベルは設定から読み込み、通常時は不要な詳細ログを抑えつつ障害調査時だけ再起動で詳細化できるようにする。
// 日次ローテーションに加えて31日を超えたファイルを削除し、長期運用でログが無制限に増えないようにする。
var fileLogger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration.GetSection("DiscaScout:FileLogging"))
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

builder.Services.AddHttpClient<DiscasPageFetcher>();
builder.Services.AddHttpClient("discord-webhook");
builder.Services.AddHttpClient("disc-image-cache");

builder.Services.AddDiscaScoutServices(imageCachePath);
builder.Services.AddDiscaScoutBackgroundServices();

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
