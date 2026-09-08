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

    [Fact]
    public void CreateArtistKeywordUri_日本語検索語をWindows31Jでエンコードする()
    {
        var uri = DiscasSearchTarget.CreateArtistKeywordUri("ももいろクローバー", 2);

        Assert.Contains("K=%82%E0%82%E0%82%A2%82%EB%83N%83%8D%81%5B%83o%81%5B", uri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PN=2", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("AK=", uri.OriginalString, StringComparison.OrdinalIgnoreCase);
    }
}
