using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MxIO.ApiClient.Abstractions;

using XtremeIdiots.Portal.RepositoryApi.Abstractions.Constants;
using XtremeIdiots.Portal.RepositoryApi.Abstractions.Models.Maps;
using XtremeIdiots.Portal.RepositoryApiClient;
using XtremeIdiots.Portal.SyncFunc.Redirect;

namespace XtremeIdiots.Portal.SyncFunc
{
    public class MapRedirectSync
    {
        private readonly string[] _defaultMaps =
        {
            "mp_ambush", "mp_backlot", "mp_bloc", "mp_bog", "mp_broadcast", "mp_chinatown", "mp_countdown", "mp_crash", "mp_creek", "mp_crossfire",
            "mp_district", "mp_downpour", "mp_killhouse", "mp_overgrown", "mp_pipeline", "mp_shipment", "mp_showdown", "mp_strike", "mp_vacant", "mp_cargoship",
            "mp_airfield", "mp_asylum", "mp_castle", "mp_cliffside", "mp_courtyard", "mp_dome", "mp_downfall", "mp_hanger", "mp_makin", "mp_outskirts", "mp_roundhouse",
            "mp_seelow", "mp_upheaval"
        };

        private readonly ILogger<MapRedirectSync> logger;

        public MapRedirectSync(
            ILogger<MapRedirectSync> logger,
            IRepositoryApiClient repositoryApiClient,
            IMapRedirectRepository mapRedirectRepository)
        {
            this.logger = logger;
            RepositoryApiClient = repositoryApiClient;
            MapRedirectRepository = mapRedirectRepository;
        }

        public IRepositoryApiClient RepositoryApiClient { get; }
        public IMapRedirectRepository MapRedirectRepository { get; }

        [Function(nameof(RunMapRedirectSyncManual))]
        public async Task RunMapRedirectSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await RunMapRedirectSync(null);
        }

        [Function(nameof(RunMapRedirectSync))]
        // ReSharper disable once UnusedMember.Global
        public async Task RunMapRedirectSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
        {
            logger.LogDebug($"Start RunMapRedirectSync @ {DateTime.UtcNow}");

            var stopWatch = new Stopwatch();
            stopWatch.Start();

            var gamesToSync = new Dictionary<GameType, string>
            {
                {GameType.CallOfDuty4, "cod4"},
                {GameType.CallOfDuty5, "cod5"}
            };

            foreach (var game in gamesToSync)
            {
                // Retrieve all of the maps from the redirect server
                var mapRedirectEntries = await MapRedirectRepository.GetMapEntriesForGame(game.Value);

                // Retrieve all of the maps from the repository in batches
                var skipEntries = 0;
                var takeEntries = 500;

                var repositoryMaps = new List<MapDto>();

                ApiResponseDto<MapsCollectionDto>? mapsCollectionBatch = null;
                while (mapsCollectionBatch == null || mapsCollectionBatch.Result?.Entries.Count > 0)
                {
                    mapsCollectionBatch = await RepositoryApiClient.Maps.GetMaps(game.Key, null, null, null, skipEntries, takeEntries, null);
                    repositoryMaps.AddRange(repositoryMaps);

                    skipEntries += takeEntries;
                }

                logger.LogInformation($"Total maps retrieved from redirect for '{game}' is '{mapRedirectEntries.Count}' and repository is '{repositoryMaps.Count}'");

                // Compare the map entries in the redirect to those in the repository and generate a list of additions and changes.
                var mapDtosToCreate = new List<CreateMapDto>();
                var mapDtosToUpdate = new List<EditMapDto>();

                foreach (var mapRedirectEntry in mapRedirectEntries)
                {
                    var repositoryMap = repositoryMaps.SingleOrDefault(m => m.GameType == game.Key && m.MapName == mapRedirectEntry.MapName);

                    if (repositoryMap == null)
                    {
                        var mapDtoToCreate = new CreateMapDto(game.Key, mapRedirectEntry.MapName)
                        {
                            MapFiles = mapRedirectEntry.MapFiles.Where(mf => mf.EndsWith(".iwd") || mf.EndsWith(".ff")).Select(mf =>
                                new MapFileDto(mf, $"https://redirect.xtremeidiots.net/redirect/{game.Value}/usermaps/{mapRedirectEntry.MapName}/{mf}")).ToList()
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
                                    new MapFileDto(mf, $"https://redirect.xtremeidiots.net/redirect/{game.Value}/usermaps/{mapRedirectEntry.MapName}/{mf}")).ToList()
                            });
                        }
                    }
                }

                logger.LogInformation($"Creating {mapDtosToCreate.Count} new maps and updating {mapDtosToUpdate.Count} existing maps");

                if (mapDtosToCreate.Count > 0)
                    await RepositoryApiClient.Maps.CreateMaps(mapDtosToCreate);

                if (mapDtosToUpdate.Count > 0)
                    await RepositoryApiClient.Maps.UpdateMaps(mapDtosToUpdate);
            }

            stopWatch.Stop();
            logger.LogDebug($"Stop RunMapRedirectSync @ {DateTime.UtcNow} after {stopWatch.ElapsedMilliseconds} milliseconds");
        }
    }
}