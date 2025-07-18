﻿using System.Reflection;

using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MX.Api.Client.Extensions;
using XtremeIdiots.InvisionCommunity;
using XtremeIdiots.Portal.Forums.Integration.Extensions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App;
using XtremeIdiots.Portal.Sync.App.Extensions;
using XtremeIdiots.Portal.Sync.App.Helpers;
using XtremeIdiots.Portal.Sync.App.Redirect;

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

        services.AddServersApiClient(options =>
        {
            options.WithBaseUrl(config["ServersIntegrationApi:BaseUrl"] ?? throw new ArgumentNullException("ServersIntegrationApi:BaseUrl"))
                .WithApiKeyAuthentication(config["ServersIntegrationApi:ApiKey"] ?? throw new ArgumentNullException("ServersIntegrationApi:ApiKey"))
                .WithEntraIdAuthentication(config["ServersIntegrationApi:ApplicationAudience"] ?? throw new ArgumentNullException("ServersIntegrationApi:ApplicationAudience"));
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
            options.StorageBlobEndpoint = config["appdata_storage_blob_endpoint"];
        });

        services.AddSingleton<IFtpHelper, FtpHelper>();

        services.AddMemoryCache();

        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();