using System.Diagnostics;
using System.Linq;

using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Api.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App;

public class MapRedirectSync(
    ILogger<MapRedirectSync> logger,
    IRepositoryApiClient repositoryApiClient,
    IMapRedirectRepository mapRedirectRepository,
    TelemetryClient telemetryClient)
{
    private readonly string[] _defaultMaps =
    [
        "mp_ambush", "mp_backlot", "mp_bloc", "mp_bog", "mp_broadcast", "mp_chinatown", "mp_countdown", "mp_crash", "mp_creek", "mp_crossfire",
        "mp_district", "mp_downpour", "mp_killhouse", "mp_overgrown", "mp_pipeline", "mp_shipment", "mp_showdown", "mp_strike", "mp_vacant", "mp_cargoship",
        "mp_airfield", "mp_asylum", "mp_castle", "mp_cliffside", "mp_courtyard", "mp_dome", "mp_downfall", "mp_hanger", "mp_makin", "mp_outskirts", "mp_roundhouse",
        "mp_seelow", "mp_upheaval"
    ];

    private readonly ILogger<MapRedirectSync> logger = logger;
    private readonly TelemetryClient telemetryClient = telemetryClient;

    public IRepositoryApiClient repositoryApiClient { get; } = repositoryApiClient;
    public IMapRedirectRepository MapRedirectRepository { get; } = mapRedirectRepository;
    [Function(nameof(RunMapRedirectSyncManual))]
    public async Task RunMapRedirectSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapRedirectSync(null);
    }

    [Function(nameof(RunMapRedirectSync))]
    // ReSharper disable once UnusedMember.Global
    public async Task RunMapRedirectSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
    {
        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            telemetryClient,
            nameof(RunMapRedirectSync),
            async () =>
            {
                logger.LogDebug("Start RunMapRedirectSync @ {Timestamp}", DateTime.UtcNow);

                Dictionary<GameType, string> gamesToSync = new()
                {
                    {GameType.CallOfDuty4, "cod4"},
                    {GameType.CallOfDuty5, "cod5"}
                };

                foreach (var game in gamesToSync)
                {
                    await ProcessGameMapSync(game.Key, game.Value);
                }

                logger.LogDebug("Stop RunMapRedirectSync @ {Timestamp}", DateTime.UtcNow);
            });
    }

    private async Task ProcessGameMapSync(GameType gameType, string gameKey)
    {
        logger.LogInformation("Starting map sync for game '{GameType}' with key '{GameKey}'", gameType, gameKey);
        
        try
        {
            // Retrieve all of the maps from the redirect server
            var mapRedirectEntries = await MapRedirectRepository.GetMapEntriesForGame(gameKey);

            // Retrieve all of the maps from the repository in batches
            var skipEntries = 0;
            var takeEntries = 500;

            var repositoryMaps = new List<MapDto>();
            ApiResult<CollectionModel<MapDto>>? mapsCollectionBatch = null;
            while (mapsCollectionBatch == null || (mapsCollectionBatch.Result?.Data?.Items != null && mapsCollectionBatch.Result.Data.Items.Any()))
            {
                mapsCollectionBatch = await repositoryApiClient.Maps.V1.GetMaps(gameType, null, null, null, skipEntries, takeEntries, null);
                repositoryMaps.AddRange(mapsCollectionBatch.Result?.Data?.Items ?? Enumerable.Empty<MapDto>());

                skipEntries += takeEntries;
            }

            logger.LogInformation("Total maps retrieved from redirect for '{GameType}' is '{RedirectCount}' and repository is '{RepositoryCount}'", 
                gameType, mapRedirectEntries.Count, repositoryMaps.Count);

            // Compare the map entries in the redirect to those in the repository and generate a list of additions and changes.
            var mapDtosToCreate = new List<CreateMapDto>();
            var mapDtosToUpdate = new List<EditMapDto>();

            foreach (var mapRedirectEntry in mapRedirectEntries)
            {
                var repositoryMap = repositoryMaps.SingleOrDefault(m => m.GameType == gameType && m.MapName == mapRedirectEntry.MapName);

                if (repositoryMap == null)
                {
                    var mapDtoToCreate = new CreateMapDto(gameType, mapRedirectEntry.MapName)
                    {
                        MapFiles = mapRedirectEntry.MapFiles.Where(mf => mf.EndsWith(".iwd") || mf.EndsWith(".ff")).Select(mf =>
                            new MapFileDto(mf, $"https://redirect.xtremeidiots.net/redirect/{gameKey}/usermaps/{mapRedirectEntry.MapName}/{mf}")).ToList()
                    };

                    mapDtosToCreate.Add(mapDtoToCreate);
                }
                else
                {
                    var mapFiles = mapRedirectEntry.MapFiles.Where(mf => mf.EndsWith(".iwd") || mf.EndsWith(".ff")).ToList();

                    if (mapFiles.Count != repositoryMap.MapFiles.Count)
                    {
                        mapDtosToUpdate.Add(new EditMapDto(repositoryMap.MapId)
                        {
                            MapFiles = mapFiles.Select(mf =>
                                new MapFileDto(mf, $"https://redirect.xtremeidiots.net/redirect/{gameKey}/usermaps/{mapRedirectEntry.MapName}/{mf}")).ToList()
                        });
                    }
                }
            }

            logger.LogInformation("Creating {CreateCount} new maps and updating {UpdateCount} existing maps", 
                mapDtosToCreate.Count, mapDtosToUpdate.Count);

            if (mapDtosToCreate.Count > 0)
                await repositoryApiClient.Maps.V1.CreateMaps(mapDtosToCreate);

            if (mapDtosToUpdate.Count > 0)
                await repositoryApiClient.Maps.V1.UpdateMaps(mapDtosToUpdate);
                
            logger.LogInformation("Completed map sync for game '{GameType}'", gameType);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync maps for game '{GameType}' with key '{GameKey}'", gameType, gameKey);
            throw;
        }
    }
}