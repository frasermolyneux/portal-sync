using System.Reflection;

using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;

using MX.Api.Client.Extensions;
using MX.InvisionCommunity.Api.Client;
using MX.Observability.ApplicationInsights.WorkerService;
using MX.Observability.ApplicationInsights.Jobs;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Platform.Monitoring;
using XtremeIdiots.Portal.Forums.Integration.Extensions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App;
using XtremeIdiots.Portal.Sync.App.Extensions;
using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Portal.Sync.App.Configuration;
using XtremeIdiots.Portal.Sync.App.Observability;

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
                    .Select("XtremeIdiots.Portal.Sync.App:*", environmentLabel)
                    .TrimKeyPrefix("XtremeIdiots.Portal.Sync.App:")
                    .Select("RepositoryApi:*", environmentLabel)
                    .Select("ServersIntegrationApi:*", environmentLabel)
                    .Select("XtremeIdiots:*", environmentLabel)
                    .Select("GameTracker:*", environmentLabel)
                    .Select("RedirectApi:*", environmentLabel)
                    .Select("ApplicationInsights:*", environmentLabel)
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

        ValidateTelemetryFilterConfiguration(configuration);

        services.AddLogging();
        services.AddMicrosoftIdentityWebApiAuthentication(configuration);
        services.AddAuthorization();
        services.AddSyncApplicationInsights();
        services.AddObservability(ProcessHealthTelemetry.ConfigureFiltering);
        services.AddSingleton<ProcessHealthTelemetry>();
        services.AddSingleton<IJobHealthStore>(_ => new BlobJobHealthStore(new BlobContainerClient(
            new Uri((configuration["ProcessHealth:StorageBlobEndpoint"]
                ?? throw new InvalidOperationException("ProcessHealth:StorageBlobEndpoint is required.")).TrimEnd('/') + "/process-health"),
            new DefaultAzureCredential())));
        services.AddSingleton<IJobTelemetry>(provider => new PersistentJobTelemetry(
            new ApplicationInsightsJobTelemetry(provider.GetRequiredService<IAuditLogger>(),
                provider.GetRequiredService<TelemetryClient>()),
            provider.GetRequiredService<IJobHealthStore>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PersistentJobTelemetry>>()));

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl(configuration["RepositoryApi:BaseUrl"] ?? throw new InvalidOperationException("RepositoryApi:BaseUrl configuration is required"))
            .WithEntraIdAuthentication(configuration["RepositoryApi:ApplicationAudience"] ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience configuration is required"))
            .WithCaching(true)
            .WithCachePartition("portal-sync")
            .WithCaching(RepositoryApiCacheConfiguration.ConfigureCachePolicies));

        services.AddServersApiClient(options =>
        {
            options.WithBaseUrl(configuration["ServersIntegrationApi:BaseUrl"] ?? throw new InvalidOperationException("ServersIntegrationApi:BaseUrl configuration is required"))
                .WithEntraIdAuthentication(configuration["ServersIntegrationApi:ApplicationAudience"] ?? throw new InvalidOperationException("ServersIntegrationApi:ApplicationAudience configuration is required"));
        });

        services.AddMapRedirectRepository(options => options
            .WithBaseUrl(configuration["RedirectApi:BaseUrl"] ?? throw new InvalidOperationException("RedirectApi:BaseUrl configuration is required"))
            .WithEntraIdAuthentication(configuration["RedirectApi:ApplicationAudience"] ?? throw new InvalidOperationException("RedirectApi:ApplicationAudience configuration is required")));

        services.AddInvisionApiClient(options => options
            .WithBaseUrl(configuration["XtremeIdiots:Forums:BaseUrl"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:BaseUrl configuration is required"))
            .WithApiKeyAuthentication(configuration["XtremeIdiots:Forums:ApiKey"] ?? throw new InvalidOperationException("XtremeIdiots:Forums:ApiKey configuration is required"), "key", MX.Api.Client.Configuration.ApiKeyLocation.QueryParameter)
            .WithCachePartition("portal-sync")
            .WithCaching(cache => cache.UseLibraryDefaults()));

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

static void ValidateTelemetryFilterConfiguration(IConfiguration configuration)
{
    if (string.IsNullOrWhiteSpace(configuration["AzureAppConfiguration:Endpoint"]))
    {
        return;
    }

    var requiredKeys = new[]
    {
        "ApplicationInsights:TelemetryFilter:Enabled",
        "ApplicationInsights:TelemetryFilter:Requests:Enabled",
        "ApplicationInsights:TelemetryFilter:Traces:Enabled",
        "ApplicationInsights:TelemetryFilter:CustomEvents:Enabled",
    };

    var missingKeys = requiredKeys
        .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
        .ToList();

    var hasAllowedNamePrefixes =
        !string.IsNullOrWhiteSpace(configuration["ApplicationInsights:TelemetryFilter:CustomEvents:AllowedNamePrefixes:0"]) ||
        !string.IsNullOrWhiteSpace(configuration["ApplicationInsights:TelemetryFilter:CustomEvents:AllowedNamePrefixes"]);

    if (!hasAllowedNamePrefixes)
    {
        missingKeys.Add("ApplicationInsights:TelemetryFilter:CustomEvents:AllowedNamePrefixes");
    }

    if (missingKeys.Count > 0)
    {
        throw new InvalidOperationException(
            $"Missing required App Configuration telemetry filter keys: {string.Join(", ", missingKeys)}");
    }
}
