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
    .ConfigureFunctionsWebApplication(options => { })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.AddLogging();
        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddRepositoryApiClient(options =>
        {
            options.BaseUrl = config["apim_base_url"] ?? config["repository_base_url"] ?? throw new ArgumentNullException("apim_base_url");
            options.PrimaryApiKey = config["portal_repository_apim_subscription_key_primary"] ?? throw new ArgumentNullException("portal_repository_apim_subscription_key_primary");
            options.SecondaryApiKey = config["portal_repository_apim_subscription_key_secondary"] ?? throw new ArgumentNullException("portal_repository_apim_subscription_key_secondary");
            options.ApiAudience = config["repository_api_application_audience"] ?? throw new ArgumentNullException("repository_api_application_audience");
            options.ApiPathPrefix = config["repository_api_path_prefix"] ?? "repository";
        });

        services.AddMapRedirectRepository(options =>
        {
            options.MapRedirectBaseUrl = config["map_redirect_base_url"] ?? throw new ArgumentNullException("map_redirect_base_url");
            options.ApiKey = config["map_redirect_api_key"] ?? throw new ArgumentNullException("map_redirect_api_key");
        });

        services.AddInvisionApiClient(options =>
        {
            options.BaseUrl = config["xtremeidiots_forums_base_url"] ?? throw new ArgumentNullException("xtremeidiots_forums_base_url");
            options.ApiKey = config["xtremeidiots_forums_api_key"] ?? throw new ArgumentNullException("xtremeidiots_forums_api_key");
        });

        services.AddAdminActionTopics();

        services.AddBanFilesRepository(options =>
        {
            options.ConnectionString = config["appdata_storage_connectionstring"];
        });

        services.AddSingleton<IFtpHelper, FtpHelper>();

        services.AddMemoryCache();

        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();