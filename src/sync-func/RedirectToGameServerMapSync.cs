using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.RepositoryApi.Abstractions.Constants;
using XtremeIdiots.Portal.RepositoryApiClient;
using XtremeIdiots.Portal.ServersApiClient;

namespace XtremeIdiots.Portal.SyncFunc
{
    public class RedirectToGameServerMapSync
    {
        private readonly ILogger<RedirectToGameServerMapSync> logger;
        private readonly IRepositoryApiClient repositoryApiClient;
        private readonly IServersApiClient serversApiClient;

        public RedirectToGameServerMapSync(ILogger<RedirectToGameServerMapSync> logger, IRepositoryApiClient repositoryApiClient, IServersApiClient serversApiClient)
        {
            this.logger = logger;
            this.repositoryApiClient = repositoryApiClient;
            this.serversApiClient = serversApiClient;
        }

        [Function(nameof(RunRedirectToGameServerMapSyncManual))]
        public async Task RunRedirectToGameServerMapSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await RunRedirectToGameServerMapSync(null);
        }

        [Function(nameof(RunRedirectToGameServerMapSync))]
        public async Task RunRedirectToGameServerMapSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
        {
            GameType[] gameTypes = [GameType.CallOfDuty4];
            var gameServersApiResponse = await repositoryApiClient.GameServers.GetGameServers(gameTypes, null, null, 0, 50, null);

            if (!gameServersApiResponse.IsSuccess || gameServersApiResponse.Result == null)
            {
                logger.LogCritical("Failed to retrieve game servers from repository");
                return;
            }

            foreach (var gameServerDto in gameServersApiResponse.Result.Entries)
            {
                using (logger.BeginScope(gameServerDto.TelemetryProperties))
                {
                    try
                    {
                        var rconMaps = await serversApiClient.Rcon.GetServerMaps(gameServerDto.GameServerId);
                        var serverMaps = await serversApiClient.Maps.GetLoadedServerMapsFromHost(gameServerDto.GameServerId);

                        if (!rconMaps.IsSuccess || rconMaps.Result == null)
                        {
                            logger.LogError("Failed to retrieve rcon maps for game server");
                            continue;
                        }

                        if (!serverMaps.IsSuccess || serverMaps.Result == null)
                        {
                            logger.LogError("Failed to retrieve maps for game server host");
                            continue;
                        }

                        foreach (var map in rconMaps.Result.Entries)
                        {
                            if (!serverMaps.Result.Entries.Any(x => x.Name == map.MapName))
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