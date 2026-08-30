using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// 画像キャッシュの取得、再利用、失敗時の状態を検証する
/// </summary>
public sealed class DiscImageCacheServiceTests
{
    [Fact]
    public async Task EnsureCachedAsync_画像を保存して相対パスを記録する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"discascout-image-test-{Guid.NewGuid():N}");
        try
        {
            var disc = await AddDiscAsync(database.Context, "1001", "https://example.test/1001.jpg");
            var handler = new StubHandler(_ => CreateImageResponse("image-data"));
            var service = CreateService(database.Context, handler, root);

            var result = await service.EnsureCachedAsync(disc.Id);

            Assert.True(result);
            var saved = await database.Context.Discs.SingleAsync();
            Assert.Equal("1001.jpg", saved.CachedImagePath);
            Assert.True(File.Exists(Path.Combine(root, "1001.jpg")));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EnsureCachedAsync_既存キャッシュがあれば再取得しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"discascout-image-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "1001.jpg"), "cached");
            var disc = await AddDiscAsync(database.Context, "1001", "https://example.test/1001.jpg");
            disc.CachedImagePath = "1001.jpg";
            await database.Context.SaveChangesAsync();

            var handler = new StubHandler(_ => throw new InvalidOperationException("HTTP request should not occur"));
            var service = CreateService(database.Context, handler, root);

            var result = await service.EnsureCachedAsync(disc.Id);

            Assert.True(result);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EnsureCachedAsync_取得失敗時はキャッシュパスを記録しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"discascout-image-test-{Guid.NewGuid():N}");
        try
        {
            var disc = await AddDiscAsync(database.Context, "1001", "https://example.test/1001.jpg");
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var service = CreateService(database.Context, handler, root);

            var result = await service.EnsureCachedAsync(disc.Id);

            Assert.False(result);
            var saved = await database.Context.Discs.SingleAsync();
            Assert.Null(saved.CachedImagePath);
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static DiscImageCacheService CreateService(DiscaScoutDbContext context, HttpMessageHandler handler, string root) =>
        new(context, new HttpClient(handler), Options.Create(new DiscaScoutStorageOptions { ImageCachePath = root }));

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
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responseFactory(request));
        }
    }

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
            var context = new DiscaScoutDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
