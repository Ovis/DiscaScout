using DiscaScout.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence.Tests;

/// <summary>
/// ジャンル表示名の完全パスをジャンルマスターへ解決する処理を検証する
/// </summary>
public sealed class GenreResolverTests
{
    /// <summary>同名の親子ジャンルが存在しても親子関係を含めて最深ノードを解決できることを確認する</summary>
    [Fact]
    public async Task ResolveAsync_SameNameAtDifferentDepth_ResolvesDeepestNode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var root = new Genre { ExternalId = "01", Name = "J-POP", SortOrder = 0, IsActive = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        var child = new Genre { ExternalId = "0101", Name = "J-POP", Parent = root, SortOrder = 0, IsActive = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
        dbContext.Genres.AddRange(root, child);
        await dbContext.SaveChangesAsync();

        var resolved = await new GenreResolver(dbContext).ResolveAsync(["J-POP", "J-POP"]);

        Assert.NotNull(resolved);
        Assert.Equal(child.Id, resolved.Id);
    }

    /// <summary>途中までしか一致しないパスを部分的なジャンルへ割り当てないことを確認する</summary>
    [Fact]
    public async Task ResolveAsync_UnknownChild_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DiscaScoutDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new DiscaScoutDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        dbContext.Genres.Add(new Genre { ExternalId = "01", Name = "J-POP", SortOrder = 0, IsActive = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow });
        await dbContext.SaveChangesAsync();

        var resolved = await new GenreResolver(dbContext).ResolveAsync(["J-POP", "存在しない"]);

        Assert.Null(resolved);
    }
}
