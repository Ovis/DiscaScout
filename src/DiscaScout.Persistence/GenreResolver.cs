using DiscaScout.Core;
using Microsoft.EntityFrameworkCore;

namespace DiscaScout.Persistence;

/// <summary>DISCASから取得したジャンル名の完全パスをジャンルマスターへ解決する</summary>
public sealed class GenreResolver(DiscaScoutDbContext dbContext)
{
    /// <summary>大・中・小ジャンルの完全一致パスを解決し、最深ノードを返す</summary>
    public async Task<Genre?> ResolveAsync(string? large, string? middle, string? small, CancellationToken cancellationToken = default)
    {
        var names = new[] { large, middle, small }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToArray();
        if (names.Length == 0) return null;
        var genres = await dbContext.Genres.AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        Genre? current = null;
        foreach (var name in names)
        {
            var parentId = current?.Id;
            var matches = genres.Where(x => x.ParentId == parentId && x.Name == name).ToArray();
            if (matches.Length != 1) return null;
            current = matches[0];
        }
        return current;
    }

    /// <summary>任意長のジャンルパスを完全一致で解決する</summary>
    public Task<Genre?> ResolveAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        ResolveAsync(path.ElementAtOrDefault(0), path.ElementAtOrDefault(1), path.ElementAtOrDefault(2), cancellationToken);
}
