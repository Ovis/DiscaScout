using DiscaScout.Application;
using DiscaScout.Persistence;
using DiscaScout.Scraping;

namespace DiscaScout.Web;

/// <summary>
/// DiscaScout Webアプリケーションで使用するサービス登録をまとめる拡張メソッドを提供する
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// DiscaScout固有の通常サービスをDIコンテナへ登録する
    /// </summary>
    /// <param name="services">サービス登録先</param>
    /// <param name="imageCachePath">CD画像キャッシュの保存先</param>
    internal static IServiceCollection AddDiscaScoutServices(
        this IServiceCollection services,
        string imageCachePath)
    {
        // DISCAS取得基盤
        services.AddSingleton<DiscasRequestThrottle>();
        services.AddScoped<DiscasSearchResultParser>();
        services.AddScoped<DiscasDiscDetailParser>();
        services.AddScoped<DiscasGenreMasterParser>();
        services.AddScoped<DiscasCategoryCrawler>();
        services.AddScoped<IDiscasCategoryCrawler>(sp =>
            sp.GetRequiredService<DiscasCategoryCrawler>());
        services.AddScoped<DiscasSnapshotApplier>();
        services.AddScoped<IDiscasSnapshotStore, DiscasSnapshotStore>();
        services.AddScoped<DiscasScrapeService>();
        services.AddScoped<ScrapeRunCoordinator>();

        // メタデータ補完
        services.AddScoped<GenreResolver>();
        services.AddScoped<GenreMasterService>();
        services.AddScoped(sp => new DiscImageCacheService(
            sp.GetRequiredService<DiscaScoutDbContext>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("disc-image-cache"),
            Path.GetFullPath(imageCachePath)));
        services.AddScoped<DiscDetailMetadataService>();
        services.AddSingleton<DiscDetailFetchSignal>();

        // Artist関連
        services.AddScoped<DiscasArtistCatalogCrawler>();
        services.AddScoped<IDiscasArtistCatalogCrawler>(sp =>
            sp.GetRequiredService<DiscasArtistCatalogCrawler>());
        services.AddScoped<ArtistWatchService>();
        services.AddScoped<ArtistCatalogStore>();
        services.AddScoped<ArtistCatalogCollectionService>();

        // 運用・通知
        services.AddScoped<DiscordNotificationSettingsStore>();
        services.AddScoped<DiscordNotificationService>();
        services.AddScoped<RentalHistoryImportService>();
        services.AddScoped<ScrapeOperationsStore>();
        services.AddScoped<IScrapeOperationsStore>(sp =>
            sp.GetRequiredService<ScrapeOperationsStore>());
        services.AddScoped<IScrapeOperationsQueryStore>(sp =>
            sp.GetRequiredService<ScrapeOperationsStore>());
        services.AddScoped<ScrapeGuardStore>();
        services.AddScoped<IScrapeGuardStore>(sp =>
            sp.GetRequiredService<ScrapeGuardStore>());
        services.AddScoped<IScrapeScheduleStore, ScrapeScheduleStore>();
        services.AddScoped<ManualWorkStore>();
        services.AddSingleton<ScrapeExecutionGate>();
        services.AddSingleton<ManualWorkSignal>();

        return services;
    }

    /// <summary>
    /// 定期取得や補完処理を実行するバックグラウンドサービスを登録する
    /// </summary>
    /// <param name="services">サービス登録先</param>
    internal static IServiceCollection AddDiscaScoutBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<ScrapeBackgroundService>();
        services.AddHostedService<DiscImageCacheBackgroundService>();
        services.AddHostedService<DiscDetailMetadataBackgroundService>();

        return services;
    }
}
