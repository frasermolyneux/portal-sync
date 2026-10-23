using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Redirect.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public static class ServiceCollectionExtensions
{
    public static void AddMapRedirectRepository(this IServiceCollection serviceCollection, Action<RedirectApiClientOptionsBuilder> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(configureOptions);

        serviceCollection.AddRedirectApiClient(configureOptions);
        serviceCollection.AddTransient<IMapRedirectRepository, MapRedirectRepository>();
    }
}
