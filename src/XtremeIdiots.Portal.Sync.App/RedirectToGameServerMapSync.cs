using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App;

public class RedirectToGameServerMapSync(
    ILogger<RedirectToGameServerMapSync> logger,
    IRepositoryApiClient repositoryApiClient,
    IServersApiClient serversApiClient,
    IJobTelemetry jobTelemetry)
{
    [Function(nameof(RunRedirectToGameServerMapSyncManual))]
    public async Task RunRedirectToGameServerMapSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunRedirectToGameServerMapSync(null).ConfigureAwait(false);
    }

    [Function(nameof(RunRedirectToGameServerMapSync))]
    public async Task RunRedirectToGameServerMapSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(
            nameof(RunRedirectToGameServerMapSync),
            async () =>
            {
                await ProcessGameServerMaps().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task ProcessGameServerMaps()
    {
        GameType[] gameTypes = [GameType.CallOfDuty4, GameType.CallOfDuty4x];
        var gameServersApiResponse = await repositoryApiClient.GameServers.V1.GetGameServers(gameTypes, null, null, 0, 50, null).ConfigureAwait(false);

        if (!gameServersApiResponse.IsSuccess || gameServersApiResponse.Result?.Data?.Items is null)
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
                    var getLoadedServerMapsFronHostResult = await serversApiClient.Maps.V1.GetLoadedServerMapsFromHost(gameServerDto.GameServerId).ConfigureAwait(false);

                    if (!getLoadedServerMapsFronHostResult.IsSuccess || getLoadedServerMapsFronHostResult.Result?.Data?.Items is null)
                    {
                        logger.LogError("Failed to retrieve maps for game server host");
                        continue;
                    }

                    logger.LogInformation(
                        "Loaded {LoadedMapCount} maps from host for game server '{GameServerTitle}'",
                        getLoadedServerMapsFronHostResult.Result.Data.Items.Count(),
                        gameServerDto.Title);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process game server");
                }
            }
        }
    }
}