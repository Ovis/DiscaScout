using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// Inactiveジャンルを新規メタデータへ割り当てないことを検証する
/// </summary>
public sealed class GenreMasterInactiveTests
{
    /// <summary>Inactiveノードだけが一致する場合は未解決になることを確認する</summary>
    [Fact]
    public async Task ResolveAsync_InactiveGenre_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Genres.Add(new Genre { ExternalId = "01", Name = "旧ジャンル", SortOrder = 0, IsActive = false, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        await dbContext.SaveChangesAsync();

        Assert.Null(await new GenreResolver(dbContext).ResolveAsync(["旧ジャンル"]));
    }
}
