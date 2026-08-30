using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// ジャンルマスターとDiscのEF Core関連付けを検証する
/// </summary>
public sealed class GenreMasterSchemaTests
{
    /// <summary>Discが最深Genreを外部キーで参照できることを確認する</summary>
    [Fact]
    public async Task Disc_CanReferenceDeepestGenre()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var root = new Genre { ExternalId = "01", Name = "J-POP", SortOrder = 0, IsActive = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        var leaf = new Genre { ExternalId = "0101", Name = "J-POP", Parent = root, SortOrder = 0, IsActive = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        var disc = new Disc
        {
            DiscasId = "1",
            ProductUrl = "https://example.test/1",
            Title = "作品",
            NormalizedTitle = "作品",
            Artist = "アーティスト",
            NormalizedArtist = "アーティスト",
            Genre = leaf,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow
        };
        dbContext.Discs.Add(disc);
        await dbContext.SaveChangesAsync();

        var loaded = await dbContext.Discs.AsNoTracking().Include(x => x.Genre).SingleAsync();
        Assert.Equal(leaf.Id, loaded.GenreId);
        Assert.Equal("0101", loaded.Genre!.ExternalId);
    }
}
