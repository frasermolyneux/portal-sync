using System.Reflection;

using Azure.Identity;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MX.Api.Client.Extensions;
using MX.InvisionCommunity.Api.Client;
using XtremeIdiots.Portal.Forums.Integration.Extensions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App;
using XtremeIdiots.Portal.Sync.App.Extensions;
using XtremeIdiots.Portal.Sync.App.Helpers;
using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Portal.Sync.App.Configuration;

var host = new HostBuilder()
    .ConfigureAppConfiguration(builder =>
    {
        builder.AddEnvironmentVariables();
        builder.AddUserSecrets(Assembly.GetExecutingAssembly(), true);

        var builtConfig = builder.Build();
        var appConfigEndpoint = builtConfig["AzureAppConfiguration:Endpoint"];

        if (!string.IsNullOrWhiteSpace(appConfigEndpoint))
        {
            var managedIdentityClientId = builtConfig["AzureAppConfiguration:ManagedIdentityClientId"];
            var environmentLabel = builtConfig["AzureAppConfiguration:Environment"];

            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId,
            });

            builder.AddAzureAppConfiguration(options =>
            {
                options.Connect(new Uri(appConfigEndpoint), credential)
                    .Select("RepositoryApi:*", environmentLabel)
                    .Select("ServersIntegrationApi:*", environmentLabel)
                    .Select("XtremeIdiots:*", environmentLabel)
                    .Select("GameTracker:*", environmentLabel)
                    .Select("MapRedirect:*", environmentLabel)
                    .ConfigureRefresh(refresh =>
                    {
                        refresh.Register("Sentinel", environmentLabel, refreshAll: true)
                               .SetRefreshInterval(TimeSpan.FromMinutes(5));
                    });

                options.ConfigureKeyVault(kv =>
                {
                    kv.SetCredential(credential);
                    kv.SetSecretRefreshInterval(TimeSpan.FromHours(1));
                });
            });
        }
    })
    .ConfigureFunctionsWebApplication(options =>
    {
        options.Services.AddAzureAppConfiguration();
        options.UseAzureAppConfiguration();
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        services.AddLogging();
        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl(configuration["RepositoryApi:BaseUrl"] ?? throw new InvalidOperationException("RepositoryApi:BaseUrl configuration is required"))
            .WithEntraIdAuthentication(configuration["RepositoryApi:ApplicationAudience"] ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience configuration is required")));

        services.AddServersApiClient(options =>
        {
            options.WithBaseUrl(configuration["ServersIntegrationApi:BaseUrl"] ?? throw new InvalidOperationException("ServersIntegrationApi:BaseUrl configuration is required"))
                .WithEntraIdAuthentication(configuration["ServersIntegrationApi:ApplicationAudience"] ?? throw new InvalidOperationException("ServersIntegrationApi:ApplicationAudience configuration is required"));
        });

        services.AddMapRedirectRepository(options =>
        {
            options.MapRedirectBaseUrl = configuration["MapRedirect:BaseUrl"] ?? throw new InvalidOperationException("MapRedirect:BaseUrl configuration is required");
            options.ApiKey = configuration["MapRedirect:ApiKey"] ?? throw new InvalidOperationException("MapRedirect:ApiKey configuration is required");
        });

        services.AddInvisionApiClient(options => options
            .WithBaseUrl(configuration["XtremeIdiots:Forums:BaseUrl"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:BaseUrl configuration is required"))
            .WithApiKeyAuthentication(configuration["XtremeIdiots:Forums:ApiKey"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:ApiKey configuration is required"), "key", MX.Api.Client.Configuration.ApiKeyLocation.QueryParameter));

        services.AddAdminActionTopics();

        services.AddBanFilesRepository(options =>
        {
            options.StorageBlobEndpoint = configuration["appdata_storage_blob_endpoint"];
        });

        services.Configure<MapImagesStorageOptions>(options =>
        {
            options.StorageBlobEndpoint = configuration["map_images_storage_blob_endpoint"]; // optional
            options.ContainerName = configuration["map_images_container_name"]; // optional
        });

        services.AddSingleton<IFtpHelper, FtpHelper>();

        services.AddMemoryCache();

        services.AddHealthChecks();

        // HttpClient for MapImageSync (reuse handler, modern defaults)
        services.AddHttpClient<MapImageSync>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    })
    .Build();

await host.RunAsync();