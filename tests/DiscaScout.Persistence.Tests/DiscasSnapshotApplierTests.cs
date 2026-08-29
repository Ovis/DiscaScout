using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// DISCASカテゴリスナップショットをSQLiteへ反映した際の状態遷移を検証する
/// </summary>
public sealed class DiscasSnapshotApplierTests
{
    [Fact]
    public async Task ApplyAsync_初回取得では新規CDを未チェックとして保存する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero)));

        var result = await applier.ApplyAsync(CreateSnapshot(("1001", "作品1", "アーティスト1")));

        var disc = await database.Context.Discs
            .Include(x => x.Sources)
            .Include(x => x.ReviewReasons)
            .SingleAsync();
        Assert.Equal(1, result.AddedCount);
        Assert.True(disc.NeedsReview);
        Assert.False(disc.IsArchived);
        Assert.Equal("作品1", disc.Title);
        Assert.Equal("J-POP", disc.GenreLarge);
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.New);
        Assert.Contains(disc.Sources, x => x.Category == DiscReleaseCategory.New && x.IsActive);
    }

    [Fact]
    public async Task ApplyAsync_同じスナップショットを再取得しても理由や履歴を増やさない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        var snapshot = CreateSnapshot(("1001", "作品1", "アーティスト1"));

        await applier.ApplyAsync(snapshot);
        var result = await applier.ApplyAsync(snapshot);

        var disc = await database.Context.Discs
            .Include(x => x.ReviewReasons)
            .Include(x => x.ChangeHistory)
            .SingleAsync();
        Assert.Equal(0, result.AddedCount);
        Assert.Single(disc.ReviewReasons);
        Assert.Empty(disc.ChangeHistory);
    }

    [Fact]
    public async Task ApplyAsync_意味のあるタイトル変更では履歴とTitleChanged理由を追加する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot(("1001", "作品1", "アーティスト1")));

        await applier.ApplyAsync(CreateSnapshot(("1001", "作品1 完全版", "アーティスト1")));

        var disc = await database.Context.Discs
            .Include(x => x.ReviewReasons)
            .Include(x => x.ChangeHistory)
            .SingleAsync();
        Assert.Equal("作品1 完全版", disc.Title);
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.TitleChanged);
        var history = Assert.Single(disc.ChangeHistory);
        Assert.Equal(nameof(Disc.Title), history.Field);
        Assert.Equal("作品1", history.OldValue);
        Assert.Equal("作品1 完全版", history.NewValue);
    }

    [Fact]
    public async Task ApplyAsync_2回連続でカテゴリから消えた場合にSourceをInactiveにしてArchiveする()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot(
            ("1001", "作品1", "アーティスト1"),
            ("1002", "作品2", "アーティスト2")));

        var onlyFirst = CreateSnapshot(("1001", "作品1", "アーティスト1"));
        await applier.ApplyAsync(onlyFirst);
        var afterFirstMiss = await database.Context.DiscSources.SingleAsync(x => x.Disc.DiscasId == "1002");
        Assert.True(afterFirstMiss.IsActive);
        Assert.Equal(1, afterFirstMiss.MissingCount);

        await applier.ApplyAsync(onlyFirst);
        var disc = await database.Context.Discs.Include(x => x.Sources).SingleAsync(x => x.DiscasId == "1002");
        Assert.False(disc.Sources.Single().IsActive);
        Assert.Equal(2, disc.Sources.Single().MissingCount);
        Assert.True(disc.IsArchived);
    }

    [Fact]
    public async Task ApplyAsync_Archive済みCDが再出現した場合にReappeared理由を追加する()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        var both = CreateSnapshot(
            ("1001", "作品1", "アーティスト1"),
            ("1002", "作品2", "アーティスト2"));
        var onlyFirst = CreateSnapshot(("1001", "作品1", "アーティスト1"));

        await applier.ApplyAsync(both);
        await applier.ApplyAsync(onlyFirst);
        await applier.ApplyAsync(onlyFirst);
        await applier.ApplyAsync(both);

        var disc = await database.Context.Discs
            .Include(x => x.Sources)
            .Include(x => x.ReviewReasons)
            .SingleAsync(x => x.DiscasId == "1002");
        Assert.False(disc.IsArchived);
        Assert.True(disc.Sources.Single().IsActive);
        Assert.Equal(0, disc.Sources.Single().MissingCount);
        Assert.Contains(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.Reappeared);
    }

    [Fact]
    public async Task ApplyAsync_Nfkcと空白だけの表記差ではタイトル変更理由を追加しない()
    {
        await using var database = await TestDatabase.CreateAsync();
        var applier = new DiscasSnapshotApplier(database.Context, new FixedTimeProvider(DateTimeOffset.UtcNow));
        await applier.ApplyAsync(CreateSnapshot(("1001", "ＡＢＣ 作品", "アーティスト1")));

        await applier.ApplyAsync(CreateSnapshot(("1001", "ABC   作品", "アーティスト1")));

        var disc = await database.Context.Discs
            .Include(x => x.ReviewReasons)
            .Include(x => x.ChangeHistory)
            .SingleAsync();
        Assert.Equal("ABC   作品", disc.Title);
        Assert.DoesNotContain(disc.ReviewReasons, x => x.Reason == DiscReviewReasonType.TitleChanged);
        Assert.Empty(disc.ChangeHistory);
    }

    private static DiscasCategorySnapshot CreateSnapshot(params (string Id, string Title, string Artist)[] products)
    {
        var scraped = products
            .Select((x, index) => new ScrapedDisc(
                x.Id,
                $"https://example.test/goodsDetail.do?titleID={x.Id}",
                x.Title,
                x.Artist,
                "J-POP",
                "J-POP",
                null,
                $"https://example.test/{x.Id}.jpg",
                null,
                DiscSourceCategory.New,
                index + 1))
            .ToArray();

        return new DiscasCategorySnapshot(DiscSourceCategory.New, scraped.Length, 1, scraped);
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
