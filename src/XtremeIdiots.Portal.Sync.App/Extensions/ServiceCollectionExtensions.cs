using Microsoft.Extensions.DependencyInjection;

using XtremeIdiots.Portal.Sync.App.Configuration;
using XtremeIdiots.Portal.Sync.App.Ingest;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Repository;
using XtremeIdiots.Portal.Sync.App.Validators;

namespace XtremeIdiots.Portal.Sync.App.Extensions;

public static class ServiceCollectionExtensions
{
    public static void AddBanFilesRepository(this IServiceCollection serviceCollection, Action<BanFilesRepositoryOptions> options)
    {
        serviceCollection.Configure(options);

        serviceCollection.AddScoped<IBanFilesRepository, BanFilesRepository>();
        serviceCollection.AddScoped<IBanFileIngest, BanFileIngest>();
        serviceCollection.AddScoped<IGuidValidator, GuidValidator>();
    }
}