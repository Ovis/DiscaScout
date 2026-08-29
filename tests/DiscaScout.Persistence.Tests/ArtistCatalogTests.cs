using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// Artist全作品収集の後段フィルタとCatalog状態遷移をSQLite実プロバイダーで検証する
/// </summary>
public sealed class ArtistCatalogTests
{
    [Fact]
    public async Task ApplyAsync_検索結果から設定条件に一致するCDだけCatalogへ保存する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var setting = await AddSettingAsync(database.Context, "梶浦由記", ArtistMatchType.Exact);
        var store = new ArtistCatalogStore(database.Context, new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)));

        var result = await store.ApplyAsync(
            setting.Id,
            CreateCatalogSnapshot(
                ("1001", "作品1", "梶浦由記"),
                ("1002", "参加作品", "別アーティスト")));

        Assert.Equal(2, result.SearchResultCount);
        Assert.Equal(1, result.MatchedCount);
        Assert.Equal(1, result.AddedDiscCount);

        var disc = await database.Context.Discs.SingleAsync();
        Assert.Equal("1001", disc.DiscasId);
        Assert.True(disc.IsArchived);
        Assert.False(disc.NeedsReview);

        var relation = await database.Context.DiscArtistCatalogs.SingleAsync();
        Assert.True(relation.IsActive);
        Assert.Equal(setting.Id, relation.ArtistSettingId);
    }

    [Fact]
    public async Task ApplyAsync_正常再取得で消えたCDはCatalog関係だけをInactiveにする()
    {
        await using var database = await TestDatabase.CreateAsync();
        var setting = await AddSettingAsync(database.Context, "梶浦由記", ArtistMatchType.Exact);
        var store = new ArtistCatalogStore(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));

        await store.ApplyAsync(setting.Id, CreateCatalogSnapshot(("1001", "作品1", "梶浦由記")));
        var result = await store.ApplyAsync(setting.Id, new DiscasArtistCatalogSnapshot("梶浦由記", 0, 1, []));

        Assert.Equal(1, result.DeactivatedCount);
        var relation = await database.Context.DiscArtistCatalogs.SingleAsync();
        Assert.False(relation.IsActive);
        Assert.NotNull(relation.DeactivatedAt);

        // Catalogの有効状態は通常リリースカテゴリのArchive判定とは独立している。
        var disc = await database.Context.Discs.SingleAsync();
        Assert.True(disc.IsArchived);
    }

    [Fact]
    public async Task NormalSnapshot_Catalogで先に保存したCDが通常カテゴリへ現れたらNewとして未チェックにする()
    {
        await using var database = await TestDatabase.CreateAsync();
        var setting = await AddSettingAsync(database.Context, "梶浦由記", ArtistMatchType.Exact);
        var catalogStore = new ArtistCatalogStore(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await catalogStore.ApplyAsync(setting.Id, CreateCatalogSnapshot(("1001", "作品1", "梶浦由記")));

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(1)));
        await applier.ApplyAsync(CreateNormalSnapshot(("1001", "作品1", "梶浦由記")));

        var disc = await database.Context.Discs
            .Include(x => x.Sources)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.False(disc.IsArchived);
        Assert.True(disc.NeedsReview);
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.New);
        Assert.DoesNotContain(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.Reappeared);
        Assert.Single(disc.Sources);
    }

    private static async Task<ArtistSetting> AddSettingAsync(
        DiscaScoutDbContext context,
        string artist,
        ArtistMatchType matchType)
    {
        var setting = new ArtistSetting
        {
            Artist = artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(artist),
            MatchType = matchType,
            IsWatchEnabled = true,
            CollectFullCatalog = true
        };
        context.ArtistSettings.Add(setting);
        await context.SaveChangesAsync();
        return setting;
    }

    private static DiscasArtistCatalogSnapshot CreateCatalogSnapshot(
        params (string Id, string Title, string Artist)[] products)
    {
        var scraped = products
            .Select((x, index) => CreateScrapedDisc(x.Id, x.Title, x.Artist, DiscSourceCategory.ArtistCatalog, index + 1))
            .ToArray();
        return new DiscasArtistCatalogSnapshot("梶浦由記", scraped.Length, 1, scraped);
    }

    private static DiscasCategorySnapshot CreateNormalSnapshot(
        params (string Id, string Title, string Artist)[] products)
    {
        var scraped = products
            .Select((x, index) => CreateScrapedDisc(x.Id, x.Title, x.Artist, DiscSourceCategory.New, index + 1))
            .ToArray();
        return new DiscasCategorySnapshot(DiscSourceCategory.New, scraped.Length, 1, scraped);
    }

    private static ScrapedDisc CreateScrapedDisc(
        string id,
        string title,
        string artist,
        DiscSourceCategory category,
        int rank)
    {
        return new ScrapedDisc(
            id,
            $"https://example.test/goodsDetail.do?titleID={id}",
            title,
            artist,
            "アニメ／ゲーム",
            "アニメ",
            null,
            $"https://example.test/{id}.jpg",
            null,
            category,
            rank);
    }

    /// <summary>
    /// SQLiteの実プロバイダーをメモリ上で維持するテスト用DBを管理する
    /// </summary>
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
            var options = new DbContextOptionsBuilder<DiscaScoutDbContext>()
                .UseSqlite(connection)
                .Options;
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

    /// <summary>
    /// 永続化テストで時刻を固定し、状態遷移だけを検証できるようにする
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
