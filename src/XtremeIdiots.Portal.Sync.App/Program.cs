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
        var configuration = context.Configuration;

        services.AddLogging();
        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddRepositoryApiClient(options => options
            .WithBaseUrl(configuration["RepositoryApi:BaseUrl"] ?? throw new InvalidOperationException("RepositoryApi:BaseUrl configuration is required"))
            .WithApiKeyAuthentication(configuration["RepositoryApi:ApiKey"] ?? throw new InvalidOperationException("RepositoryApi:ApiKey configuration is required"))
            .WithEntraIdAuthentication(configuration["RepositoryApi:ApplicationAudience"] ?? throw new InvalidOperationException("RepositoryApi:ApplicationAudience configuration is required")));

        services.AddServersApiClient(options =>
        {
            options.WithBaseUrl(configuration["ServersIntegrationApi:BaseUrl"] ?? throw new ArgumentNullException("ServersIntegrationApi:BaseUrl"))
                .WithApiKeyAuthentication(configuration["ServersIntegrationApi:ApiKey"] ?? throw new ArgumentNullException("ServersIntegrationApi:ApiKey"))
                .WithEntraIdAuthentication(configuration["ServersIntegrationApi:ApplicationAudience"] ?? throw new ArgumentNullException("ServersIntegrationApi:ApplicationAudience"));
        });

        services.AddMapRedirectRepository(options =>
        {
            options.MapRedirectBaseUrl = configuration["map_redirect_base_url"] ?? throw new ArgumentNullException("map_redirect_base_url");
            options.ApiKey = configuration["map_redirect_api_key"] ?? throw new ArgumentNullException("map_redirect_api_key");
        });

        services.AddInvisionApiClient(options =>
        {
            options.BaseUrl = configuration["xtremeidiots_forums_base_url"] ?? throw new ArgumentNullException("xtremeidiots_forums_base_url");
            options.ApiKey = configuration["xtremeidiots_forums_api_key"] ?? throw new ArgumentNullException("xtremeidiots_forums_api_key");
        });

        services.AddAdminActionTopics();

        services.AddBanFilesRepository(options =>
        {
            options.StorageBlobEndpoint = configuration["appdata_storage_blob_endpoint"];
        });

        services.AddSingleton<IFtpHelper, FtpHelper>();

        services.AddMemoryCache();

        services.AddHealthChecks();
    })
    .Build();

await host.RunAsync();