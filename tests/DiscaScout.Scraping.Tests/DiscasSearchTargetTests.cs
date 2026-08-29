using DiscaScout.Scraping;

namespace DiscaScout.Scraping.Tests;

/// <summary>
/// DISCAS検索URLの生成規則を検証する
/// </summary>
public sealed class DiscasSearchTargetTests
{
    [Fact]
    public void CreateArtistUri_日本語アーティスト名をWindows31Jでエンコードする()
    {
        var uri = DiscasSearchTarget.CreateArtistUri("梶浦由記", 3);

        Assert.Contains("AK=%8A%81%89%59%97%52%8B%4C", uri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AKN=%8A%81%89%59%97%52%8B%4C", uri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PN=3", uri.Query, StringComparison.Ordinal);
    }
}
