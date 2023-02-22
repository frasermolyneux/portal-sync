using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.RepositoryApi.Abstractions.Constants;
using XtremeIdiots.Portal.RepositoryApiClient;

namespace XtremeIdiots.Portal.SyncFunc
{
    public class MapImageSync
    {
        private const int TakeEntries = 50;
        private readonly ILogger<MapImageSync> logger;
        private readonly IRepositoryApiClient repositoryApiClient;

        public MapImageSync(
            ILogger<MapImageSync> logger,
            IRepositoryApiClient repositoryApiClient)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
        }

        [Function(nameof(RunMapImageSyncManual))]
        public async Task RunMapImageSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await RunMapImageSync(null);
        }

        [Function(nameof(RunMapImageSync))]
        public async Task RunMapImageSync([TimerTrigger("0 0 0 * * 3")] TimerInfo myTimer)
        {
            var gamesToSync = new Dictionary<GameType, string>
            {
                {GameType.CallOfDuty2, "cod2"},
                {GameType.CallOfDuty4, "cod4"},
                {GameType.CallOfDuty5, "codww"},
                {GameType.UnrealTournament2004, "ut2k4"},
                {GameType.Insurgency, "ins"},
            };

            foreach (var game in gamesToSync)
            {
                var skip = 0;
                var mapsResponseDto = await repositoryApiClient.Maps.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null);

                do
                {
                    logger.LogInformation($"Processing '{mapsResponseDto.Result.Entries.Count}' maps for '{game.Key}'");

                    foreach (var mapDto in mapsResponseDto.Result.Entries)
                    {
                        var gameTrackerImageUrl = $"https://image.gametracker.com/images/maps/160x120/{game.Value}/{mapDto.MapName}.jpg";

                        try
                        {
                            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

                            using (var client = new WebClient())
                            {
                                client.Headers.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/71.0.3578.98 Safari/537.36");

                                var filePath = Path.GetTempFileName();
                                client.DownloadFile(new Uri(gameTrackerImageUrl), filePath);

                                await repositoryApiClient.Maps.UpdateMapImage(mapDto.MapId, filePath);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, $"Failed to retrieve map image from {gameTrackerImageUrl}");
                        }
                    }

                    skip += TakeEntries;
                    mapsResponseDto = await repositoryApiClient.Maps.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null);
                } while (mapsResponseDto.Result.Entries.Any());
            }
        }
    }
}