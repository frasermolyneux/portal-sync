using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Observability;

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
        var servers = new List<Portal.Repository.Abstractions.Models.V1.GameServers.GameServerDto>();
        var identities = new HashSet<Guid>();
        while (true)
        {
            var response = await repositoryApiClient.GameServers.V1.GetGameServers(gameTypes, null, null, servers.Count, 50, null).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result?.Data?.Items is null)
            {
                logger.LogCritical("Failed to retrieve the complete game server inventory from repository");
                throw new ApplicationException("Failed to retrieve the complete game server inventory from repository");
            }
            var page = response.Result.Data.Items.ToArray();
            if (page.Length > 50 || page.Any(server => !identities.Add(server.GameServerId)))
            {
                throw new InvalidDataException("Repeated game server identities prevent a complete map inventory.");
            }
            servers.AddRange(page);
            if (page.Length < 50)
            {
                break;
            }
        }
        var failures = new List<Exception>();
        foreach (var gameServerDto in servers)
        {
            using (logger.BeginScope(gameServerDto.TelemetryProperties))
            {
                try
                {
                    var getLoadedServerMapsFronHostResult = await serversApiClient.Maps.V1.GetLoadedServerMapsFromHost(gameServerDto.GameServerId).ConfigureAwait(false);

                    if (!getLoadedServerMapsFronHostResult.IsSuccess || getLoadedServerMapsFronHostResult.Result?.Data?.Items is null)
                    {
                        throw new HttpRequestException("Failed to retrieve maps for game server host.");
                    }

                    logger.LogInformation(
                        "Loaded {LoadedMapCount} maps from host for game server '{GameServerTitle}'",
                        getLoadedServerMapsFronHostResult.Result.Data.Items.Count(),
                        gameServerDto.Title);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process game server");
                    failures.Add(ex);
                }
            }
        }
        if (failures.Count > 0)
        {
            throw new PartialJobFailureException(servers.Count, failures);
        }
    }
}
