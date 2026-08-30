using DiscaScout.Core;
using DiscaScout.Persistence;
using DiscaScout.Scraping;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// レンタル履歴由来CDが通常クロールの状態遷移で失われないことを検証する
/// </summary>
public sealed class RentalHistoryPersistenceTests
{
    [Fact]
    public async Task ApplyAsync_通常Sourceがなくても履歴由来CDはArchiveしない()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        dbContext.Discs.Add(new Disc
        {
            DiscasId = "0000102452",
            ProductUrl = "https://www.discas.net/netdvd/cd/goodsDetail.do?titleID=0000102452",
            Title = "断絶",
            NormalizedTitle = DiscTextNormalizer.Normalize("断絶"),
            Artist = "井上陽水",
            NormalizedArtist = DiscTextNormalizer.Normalize("井上陽水"),
            FirstSeenAt = now,
            LastSeenAt = now,
            LastUpdatedAt = now,
            IsRented = true,
            RentalHistoryImportedAt = now
        });
        await dbContext.SaveChangesAsync();

        var snapshotDisc = new ScrapedDisc(
            "9999999999",
            "https://example.test/goodsDetail.do?titleID=9999999999",
            "別作品",
            "別Artist",
            "J-POP",
            "J-POP",
            null,
            null,
            null,
            DiscSourceCategory.New,
            1);
        var snapshot = new DiscasCategorySnapshot(DiscSourceCategory.New, 1, 1, [snapshotDisc]);
        var applier = new DiscasSnapshotApplier(dbContext);

        await applier.ApplyAsync(snapshot);

        var imported = await dbContext.Discs.SingleAsync(x => x.DiscasId == "0000102452");
        Assert.False(imported.IsArchived);
        Assert.True(imported.IsRented);
        Assert.NotNull(imported.RentalHistoryImportedAt);
    }
}
