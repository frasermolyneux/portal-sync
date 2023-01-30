using System.Reflection;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using XtremeIdiots.InvisionCommunity;
using XtremeIdiots.Portal.ForumsIntegration.Extensions;
using XtremeIdiots.Portal.RepositoryApiClient;
using XtremeIdiots.Portal.SyncFunc;
using XtremeIdiots.Portal.SyncFunc.Extensions;
using XtremeIdiots.Portal.SyncFunc.Helpers;
using XtremeIdiots.Portal.SyncFunc.Redirect;

var host = new HostBuilder()
    .ConfigureAppConfiguration(builder =>
    {
        builder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);
    })
    .ConfigureFunctionsWorkerDefaults(builder =>
    {
        builder
            .AddApplicationInsights()
            .AddApplicationInsightsLogger();
    })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.AddRepositoryApiClient(options =>
        {
            options.BaseUrl = config["apim_base_url"] ?? config["repository_base_url"];
            options.ApiKey = config["portal_repository_apim_subscription_key"];
            options.ApiPathPrefix = config["repository_api_path_prefix"] ?? "repository";
        });

        services.AddMapRedirectRepository(options =>
        {
            options.MapRedirectBaseUrl = config["map_redirect_base_url"];
            options.ApiKey = config["map_redirect_api_key"];
        });

        services.AddInvisionApiClient(options =>
        {
            options.BaseUrl = config["xtremeidiots_forums_base_url"];
            options.ApiKey = config["xtremeidiots_forums_api_key"];
        });

        services.AddAdminActionTopics();

        services.AddBanFilesRepository(options =>
        {
            options.ConnectionString = config["appdata_storage_connectionstring"];
        });

        services.AddSingleton<IFtpHelper, FtpHelper>();

        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        services.AddLogging();
        services.AddMemoryCache();
    })
    .Build();

await host.RunAsync();