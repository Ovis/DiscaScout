using System.Net;
using System.Net.Http.Headers;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// ジャケット画像キャッシュの取得・再利用・安全な差し替えをSQLite実プロバイダーで検証する
/// </summary>
public sealed class DiscImageCacheServiceTests
{
    [Fact]
    public async Task SyncAsync_初回取得後は同じImageUrlを再取得しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = await AddDiscAsync(database.Context, "1001", "https://images.example.test/1001.jpg");
        using var handler = new RecordingHandler(CreateImageResponse("first"));
        using var client = new HttpClient(handler);
        using var directory = new TemporaryDirectory();
        var service = new DiscImageCacheService(database.Context, client, directory.Path, TimeSpan.Zero);

        var first = await service.SyncAsync([disc.DiscasId]);
        var second = await service.SyncAsync([disc.DiscasId]);

        Assert.Equal(1, first.CachedCount);
        Assert.Equal(1, second.SkippedCount);
        Assert.Equal(1, handler.RequestCount);

        await database.Context.Entry(disc).ReloadAsync();
        Assert.NotNull(disc.ImagePath);
        Assert.True(File.Exists(disc.ImagePath));
        Assert.Equal("first", await File.ReadAllTextAsync(disc.ImagePath));
    }

    [Fact]
    public async Task SyncAsync_ImageUrl変更時は新画像確定後に旧ファイルを削除する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = await AddDiscAsync(database.Context, "1001", "https://images.example.test/old.jpg");
        using var handler = new RecordingHandler(CreateImageResponse("old"), CreateImageResponse("new"));
        using var client = new HttpClient(handler);
        using var directory = new TemporaryDirectory();
        var service = new DiscImageCacheService(database.Context, client, directory.Path, TimeSpan.Zero);

        await service.SyncAsync([disc.DiscasId]);
        await database.Context.Entry(disc).ReloadAsync();
        var oldPath = Assert.IsType<string>(disc.ImagePath);
        Assert.True(File.Exists(oldPath));

        disc.ImageUrl = "https://images.example.test/new.jpg";
        await database.Context.SaveChangesAsync();
        var result = await service.SyncAsync([disc.DiscasId]);

        await database.Context.Entry(disc).ReloadAsync();
        Assert.Equal(1, result.CachedCount);
        Assert.NotEqual(oldPath, disc.ImagePath);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(disc.ImagePath!));
        Assert.Equal("new", await File.ReadAllTextAsync(disc.ImagePath!));
    }

    [Fact]
    public async Task SyncAsync_新画像取得失敗時は既存ImagePathを維持する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = await AddDiscAsync(database.Context, "1001", "https://images.example.test/old.jpg");
        using var handler = new RecordingHandler(CreateImageResponse("old"), new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var directory = new TemporaryDirectory();
        var service = new DiscImageCacheService(database.Context, client, directory.Path, TimeSpan.Zero);

        await service.SyncAsync([disc.DiscasId]);
        await database.Context.Entry(disc).ReloadAsync();
        var oldPath = Assert.IsType<string>(disc.ImagePath);

        disc.ImageUrl = "https://images.example.test/new.jpg";
        await database.Context.SaveChangesAsync();
        var result = await service.SyncAsync([disc.DiscasId]);

        await database.Context.Entry(disc).ReloadAsync();
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(oldPath, disc.ImagePath);
        Assert.True(File.Exists(oldPath));
    }

    [Fact]
    public async Task SyncAsync_画像未登録へ変化したらImagePathを解除して旧ファイルを削除する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = await AddDiscAsync(database.Context, "1001", "https://images.example.test/old.jpg");
        using var handler = new RecordingHandler(CreateImageResponse("old"));
        using var client = new HttpClient(handler);
        using var directory = new TemporaryDirectory();
        var service = new DiscImageCacheService(database.Context, client, directory.Path, TimeSpan.Zero);

        await service.SyncAsync([disc.DiscasId]);
        await database.Context.Entry(disc).ReloadAsync();
        var oldPath = Assert.IsType<string>(disc.ImagePath);

        disc.ImageUrl = null;
        await database.Context.SaveChangesAsync();
        var result = await service.SyncAsync([disc.DiscasId]);

        await database.Context.Entry(disc).ReloadAsync();
        Assert.Equal(1, result.ClearedCount);
        Assert.Null(disc.ImagePath);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(1, handler.RequestCount);
    }

    private static async Task<Disc> AddDiscAsync(DiscaScoutDbContext context, string discasId, string imageUrl)
    {
        var now = DateTime.UtcNow;
        var disc = new Disc
        {
            DiscasId = discasId,
            ProductUrl = $"https://example.test/goodsDetail.do?titleID={discasId}",
            Title = "作品",
            NormalizedTitle = DiscTextNormalizer.Normalize("作品"),
            Artist = "アーティスト",
            NormalizedArtist = DiscTextNormalizer.Normalize("アーティスト"),
            ImageUrl = imageUrl,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now
        };
        context.Discs.Add(disc);
        await context.SaveChangesAsync();
        return disc;
    }

    private static HttpResponseMessage CreateImageResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    /// <summary>指定順のHTTPレスポンスを返し、実際の画像リクエスト回数を記録する</summary>
    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (queue.Count == 0) throw new InvalidOperationException("想定外のHTTP要求が発生した");
            var response = queue.Dequeue();
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    /// <summary>SQLiteインメモリDBを接続期間中維持する</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(SqliteConnection connection, DiscaScoutDbContext context)
        {
            Connection = connection;
            Context = context;
        }
        public SqliteConnection Connection { get; }
        public DiscaScoutDbContext Context { get; }
        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
            return new TestDatabase(connection, new DiscaScoutDbContext(options));
        }
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>テストごとに一時画像ディレクトリを作成し、終了時に削除する</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"discascout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
