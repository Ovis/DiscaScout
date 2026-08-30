using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// Artist Watchの照合と再評価を検証する
/// </summary>
public sealed class ArtistWatchTests
{
    [Fact]
    public async Task ApplyAsync_Exact一致でArtistMatched理由を追加する()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "Ado",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ado"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品", "Ado"));

        var disc = await database.Context.Discs.Include(x => x.ReviewReasons).Include(x => x.ArtistMatches).SingleAsync();
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.True(disc.ArtistMatches.Single().IsCurrentMatch);
    }

    [Fact]
    public async Task ApplyAsync_Exact一致は正規化後の表記で判定する()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "Ａｄｏ",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ａｄｏ"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品", "Ado"));

        var disc = await database.Context.Discs.Include(x => x.ReviewReasons).SingleAsync();
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    [Fact]
    public async Task ApplyAsync_Contains一致で部分一致する()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "Ado",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ado"),
            MatchType = ArtistMatchType.Contains,
            IsWatchEnabled = true
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品", "Ado feat. 初音ミク"));

        var disc = await database.Context.Discs.Include(x => x.ReviewReasons).SingleAsync();
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    [Fact]
    public async Task ApplyAsync_無効なWatchは一致理由を追加しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.ArtistSettings.Add(new ArtistSetting
        {
            Artist = "Ado",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ado"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = false
        });
        await database.Context.SaveChangesAsync();

        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot("1001", "作品", "Ado"));

        var disc = await database.Context.Discs.Include(x => x.ReviewReasons).SingleAsync();
        Assert.DoesNotContain(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
    }

    [Fact]
    public async Task ReevaluateAsync_Watch追加後に既存CDを再評価する()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Discs.Add(CreateDisc("1001", "作品", "Ado"));
        await database.Context.SaveChangesAsync();

        var setting = new ArtistSetting
        {
            Artist = "Ado",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ado"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        };
        database.Context.ArtistSettings.Add(setting);
        await database.Context.SaveChangesAsync();

        await ArtistWatchService.ReevaluateAsync(database.Context, setting.Id, DateTime.UtcNow);

        var saved = await database.Context.Discs.Include(x => x.ReviewReasons).Include(x => x.ArtistMatches).SingleAsync();
        Assert.Contains(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.True(saved.ArtistMatches.Single().IsCurrentMatch);
    }

    [Fact]
    public async Task ReevaluateAsync_レンタル済みCDは一致しても未チェックへ戻さない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var disc = CreateDisc("1001", "作品", "Ado");
        disc.IsRented = true;
        disc.NeedsReview = false;
        database.Context.Discs.Add(disc);
        var setting = new ArtistSetting
        {
            Artist = "Ado",
            NormalizedArtist = DiscTextNormalizer.Normalize("Ado"),
            MatchType = ArtistMatchType.Exact,
            IsWatchEnabled = true
        };
        database.Context.ArtistSettings.Add(setting);
        await database.Context.SaveChangesAsync();

        await ArtistWatchService.ReevaluateAsync(database.Context, setting.Id, DateTime.UtcNow);

        var saved = await database.Context.Discs.Include(x => x.ReviewReasons).Include(x => x.ArtistMatches).SingleAsync();
        Assert.False(saved.NeedsReview);
        Assert.DoesNotContain(saved.ReviewReasons, x => x.Reason == DiscReviewReasonType.ArtistMatched);
        Assert.True(saved.ArtistMatches.Single().IsCurrentMatch);
    }

    private static Disc CreateDisc(string id, string title, string artist)
    {
        var now = DateTime.UtcNow;
        return new Disc
        {
            DiscasId = id,
            ProductUrl = $"https://example.test/{id}",
            Title = title,
            NormalizedTitle = DiscTextNormalizer.Normalize(title),
            Artist = artist,
            NormalizedArtist = DiscTextNormalizer.Normalize(artist),
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now
        };
    }

    private static DiscasCategorySnapshot CreateSnapshot(string id, string title, string artist)
    {
        var product = new ScrapedDisc(
            id,
            $"https://example.test/{id}",
            title,
            artist,
            "J-POP",
            "J-POP",
            null,
            null,
            null,
            DiscSourceCategory.New,
            1);
        return new DiscasCategorySnapshot(DiscSourceCategory.New, 1, 1, [product]);
    }

    /// <summary>SQLiteのインメモリDBをテスト中維持する</summary>
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

    /// <summary>状態遷移検証用に現在時刻を固定する</summary>
    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
