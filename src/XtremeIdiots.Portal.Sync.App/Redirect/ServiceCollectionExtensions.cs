using Microsoft.Extensions.DependencyInjection;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public static class ServiceCollectionExtensions
{
    public static void AddMapRedirectRepository(this IServiceCollection serviceCollection, Action<MapRedirectRepositoryOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        serviceCollection.Configure(configureOptions);

        serviceCollection.AddSingleton(configureOptions);
        serviceCollection.AddHttpClient<IMapRedirectRepository, MapRedirectRepository>(client =>
        {
            // Base address optional; left null because full URLs are constructed dynamically.
            client.Timeout = TimeSpan.FromSeconds(30);
        });

    }
}