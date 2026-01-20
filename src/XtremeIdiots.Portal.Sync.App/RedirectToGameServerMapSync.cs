using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App
{
    public class RedirectToGameServerMapSync
    {
        private readonly ILogger<RedirectToGameServerMapSync> logger;
        private readonly IRepositoryApiClient repositoryApiClient;
        private readonly IServersApiClient serversApiClient;
        private readonly TelemetryClient telemetryClient;

        public RedirectToGameServerMapSync(ILogger<RedirectToGameServerMapSync> logger, IRepositoryApiClient repositoryApiClient, IServersApiClient serversApiClient, TelemetryClient telemetryClient)
        {
            this.logger = logger;
            this.repositoryApiClient = repositoryApiClient;
            this.serversApiClient = serversApiClient;
            this.telemetryClient = telemetryClient;
        }

        [Function(nameof(RunRedirectToGameServerMapSyncManual))]
        public async Task RunRedirectToGameServerMapSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await RunRedirectToGameServerMapSync(null);
        }

        [Function(nameof(RunRedirectToGameServerMapSync))]
        public async Task RunRedirectToGameServerMapSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
        {
            await ScheduledJobTelemetry.ExecuteWithTelemetry(
                telemetryClient,
                nameof(RunRedirectToGameServerMapSync),
                async () =>
                {
                    await ProcessGameServerMaps();
                });
        }

        private async Task ProcessGameServerMaps()
        {
            GameType[] gameTypes = [GameType.CallOfDuty4];
            var gameServersApiResponse = await repositoryApiClient.GameServers.V1.GetGameServers(gameTypes, null, null, 0, 50, null);

            if (!gameServersApiResponse.IsSuccess || gameServersApiResponse.Result?.Data?.Items == null)
            {
                logger.LogCritical("Failed to retrieve game servers from repository");
                throw new ApplicationException("Failed to retrieve game servers from repository");
            }

            foreach (var gameServerDto in gameServersApiResponse.Result.Data.Items)
            {
                using (logger.BeginScope(gameServerDto.TelemetryProperties))
                {
                    try
                    {
                        var getServerMapsResult = await serversApiClient.Rcon.V1.GetServerMaps(gameServerDto.GameServerId);
                        var getLoadedServerMapsFronHostResult = await serversApiClient.Maps.V1.GetLoadedServerMapsFromHost(gameServerDto.GameServerId);

                        if (!getServerMapsResult.IsSuccess || getServerMapsResult.Result?.Data?.Items == null)
                        {
                            logger.LogError("Failed to retrieve rcon maps for game server");
                            continue;
                        }

                        if (!getLoadedServerMapsFronHostResult.IsSuccess || getLoadedServerMapsFronHostResult.Result?.Data?.Items == null)
                        {
                            logger.LogError("Failed to retrieve maps for game server host");
                            continue;
                        }

                        foreach (var map in getServerMapsResult.Result.Data.Items)
                        {
                            if (!getLoadedServerMapsFronHostResult.Result.Data.Items.Any(x => x.Name == map.MapName))
                            {
                                logger.LogInformation($"Pushing map '{map.MapName}' to game server '{gameServerDto.Title}'");
                                //await serversApiClient.Maps.PushServerMap(gameServerDto.GameServerId, map.Name);
                            }
                            else
                            {
                                logger.LogInformation($"Map '{map.MapName}' already exists on game server '{gameServerDto.Title}'");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to process game server");
                    }
                }
            }
        }
    }
}