using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// Artist Watchの一致状態とInbox再オープン条件を検証する
/// </summary>
public sealed class ArtistWatchTests
{
    [Theory]
    [InlineData(ArtistMatchType.Exact, "梶浦由記", true)]
    [InlineData(ArtistMatchType.Exact, "梶浦由記 / FictionJunction", false)]
    [InlineData(ArtistMatchType.Contains, "梶浦由記 / FictionJunction", true)]
    public void Matcher_設定した一致方法で正規化済みArtistを判定する(
        ArtistMatchType matchType,
        string discArtist,
        bool expected)
    {
        var setting = new ArtistSetting
        {
            Artist = "梶浦由記",
            NormalizedArtist = DiscTextNormalizer.Normalize("梶浦由記"),
            MatchType = matchType
        };

        Assert.Equal(expected, ArtistWatchMatcher.IsMatch(DiscTextNormalizer.Normalize(discArtist), setting));
    }

    [Fact]
    public async Task CreateAsync_既存一致CDは履歴へ登録するが指定なしではInboxを再オープンしない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = CreateDisc("1001", "作品1", "梶浦由記");
        disc.NeedsReview = false;
        disc.LastReviewedAt = DateTimeOffset.UtcNow;
        database.Context.Discs.Add(disc);
        await database.Context.SaveChangesAsync();

        var service = new ArtistWatchService(database.Context, new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)));
        var setting = await service.CreateAsync("梶浦由記", ArtistMatchType.Exact, true, false, false);

        var saved = await database.Context.Discs
            .Include(x => x.ArtistMatches)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.False(saved.NeedsReview);
        Assert.DoesNotContain(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.Contains(saved.ArtistMatches, x => x.ArtistSettingId == setting.Id && x.IsCurrentMatch);
    }

    [Fact]
    public async Task CreateAsync_既存一致CDを戻す指定ではArtistMatched理由を追加する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = CreateDisc("1001", "作品1", "梶浦由記");
        disc.NeedsReview = false;
        database.Context.Discs.Add(disc);
        await database.Context.SaveChangesAsync();

        var service = new ArtistWatchService(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await service.CreateAsync("梶浦由記", ArtistMatchType.Exact, true, false, true);

        var saved = await database.Context.Discs.Include(x => x.ReviewReasons).SingleAsync();
        Assert.True(saved.NeedsReview);
        Assert.Contains(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    [Fact]
    public async Task ApplyAsync_新規Watch一致はArtistMatchedを付与し再取得では重複させない()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "梶浦由記",
            NormalizedArtist = DiscTextNormalizer.Normalize("梶浦由記"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        var snapshot = CreateSnapshot("1001", "作品1", "梶浦由記");
        await applier.ApplyAsync(snapshot);
        await applier.ApplyAsync(snapshot);

        var saved = await database.Context.Discs
            .Include(x => x.ArtistMatches)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.Single(saved.ArtistMatches);
        Assert.True(saved.ArtistMatches.Single().IsCurrentMatch);
        Assert.Single(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    [Fact]
    public async Task ApplyAsync_Artist変更で新規一致した場合だけArtistMatchedで再オープンする()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "梶浦由記",
            NormalizedArtist = DiscTextNormalizer.Normalize("梶浦由記"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品1", "別アーティスト"));

        var disc = await database.Context.Discs.Include(x => x.ReviewReasons).SingleAsync();
        disc.ReviewReasons.Clear();
        disc.NeedsReview = false;
        await database.Context.SaveChangesAsync();

        await applier.ApplyAsync(CreateSnapshot("1001", "作品1", "梶浦由記"));

        var saved = await database.Context.Discs
            .Include(x => x.ArtistMatches)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.True(saved.NeedsReview);
        Assert.Contains(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.True(saved.ArtistMatches.Single().IsCurrentMatch);
    }

    [Fact]
    public async Task ApplyAsync_レンタル済みCDが新規一致してもInboxは再オープンしない()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "梶浦由記",
            NormalizedArtist = DiscTextNormalizer.Normalize("梶浦由記"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        });
        var disc = CreateDisc("1001", "作品1", "別アーティスト");
        disc.IsRented = true;
        database.Context.Discs.Add(disc);
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品1", "梶浦由記"));

        var saved = await database.Context.Discs
            .Include(x => x.ArtistMatches)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.False(saved.NeedsReview);
        Assert.DoesNotContain(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.True(saved.ArtistMatches.Single().IsCurrentMatch);
    }

    private static Disc CreateDisc(string id, string title, string artist)
    {
        var now = DateTimeOffset.UtcNow;
        return new Disc
        {
            DiscasId = id,
            ProductUrl = $"https://example.test/{id}",
            Title = title,
            NormalizedTitle = DiscTextNormalizer.Normalize(title),
            Artist = artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(artist),
            GenreLarge = "J-POP",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now
        };
    }

    private static DiscasCategorySnapshot CreateSnapshot(string id, string title, string artist)
    {
        var product = new ScrapedDisc(
            id,
            $"https://example.test/goodsDetail.do?titleID={id}",
            title,
            artist,
            "J-POP",
            "J-POP",
            null,
            $"https://example.test/{id}.jpg",
            null,
            DiscSourceCategory.New,
            1);
        return new DiscasCategorySnapshot(DiscSourceCategory.New, 1, 1, [product]);
    }

    /// <summary>
    /// SQLite実プロバイダーをメモリ上で維持するArtist Watchテスト用DB
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
    /// テスト中の一致時刻を固定する
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
