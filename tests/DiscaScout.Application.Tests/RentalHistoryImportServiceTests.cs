using DiscaScout.Application;
using DiscaScout.Core;
using DiscaScout.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Application.Tests;

/// <summary>
/// レンタル履歴インポートの新規作成、既存更新、冪等性を検証する
/// </summary>
public sealed class RentalHistoryImportServiceTests
{
    [Fact]
    public async Task ImportAsync_新規と既存をレンタル済みにして再実行しても重複しない()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow.AddDays(-1);
        var existing = new Disc
        {
            DiscasId = "1234567890",
            ProductUrl = "https://example.invalid/existing",
            Title = "既存CD",
            NormalizedTitle = DiscTextNormalizer.Normalize("既存CD"),
            Artist = "既存Artist",
            NormalizedArtist = DiscTextNormalizer.Normalize("既存Artist"),
            GenreLarge = "J-POP",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            NeedsReview = true
        };
        existing.ReviewReasons.Add(new DiscReviewReason { Reason = DiscReviewReasonType.New, CreatedAt = now });
        dbContext.Discs.Add(existing);
        await dbContext.SaveChangesAsync();

        var service = new RentalHistoryImportService(dbContext);
        var entries = new[]
        {
            new RentalHistoryImportEntry("1234567890", "履歴側タイトル", "履歴側Artist"),
            new RentalHistoryImportEntry("0000102452", "断絶", "井上陽水")
        };

        var first = await service.ImportAsync(entries);

        Assert.Equal(2, first.InputCount);
        Assert.Equal(1, first.CreatedCount);
        Assert.Equal(2, first.MarkedRentedCount);
        Assert.Equal(0, first.AlreadyRentedCount);
        Assert.Equal(2, await dbContext.Discs.CountAsync());

        var refreshedExisting = await dbContext.Discs.Include(x => x.ReviewReasons).SingleAsync(x => x.DiscasId == "1234567890");
        Assert.True(refreshedExisting.IsRented);
        Assert.False(refreshedExisting.NeedsReview);
        Assert.Empty(refreshedExisting.ReviewReasons);
        Assert.NotNull(refreshedExisting.RentalHistoryImportedAt);
        // 既存の通常クロール由来メタデータは履歴画面の表示値で上書きしない。
        Assert.Equal("既存CD", refreshedExisting.Title);

        var imported = await dbContext.Discs.SingleAsync(x => x.DiscasId == "0000102452");
        Assert.Equal("0000102452", imported.DiscasId);
        Assert.Equal("断絶", imported.Title);
        Assert.Equal("井上陽水", imported.Artist);
        Assert.Equal("未取得", imported.GenreLarge);
        Assert.True(imported.IsRented);
        Assert.False(imported.NeedsReview);
        Assert.False(imported.IsArchived);
        Assert.NotNull(imported.RentalHistoryImportedAt);

        var second = await service.ImportAsync(entries);

        Assert.Equal(0, second.CreatedCount);
        Assert.Equal(0, second.MarkedRentedCount);
        Assert.Equal(2, second.AlreadyRentedCount);
        Assert.Equal(2, await dbContext.Discs.CountAsync());
    }

    [Fact]
    public async Task ImportAsync_先頭ゼロを含むtitleIdを文字列のまま保持する()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var service = new RentalHistoryImportService(dbContext);

        await service.ImportAsync([new RentalHistoryImportEntry("0001515967", "救済の技法", "平沢進")]);

        var disc = await dbContext.Discs.SingleAsync();
        Assert.Equal("0001515967", disc.DiscasId);
        Assert.EndsWith("titleID=0001515967", disc.ProductUrl, StringComparison.Ordinal);
    }
}
