using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App
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
        public async Task RunMapImageSync([TimerTrigger("0 0 0 * * 3")] TimerInfo? myTimer)
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
                var mapsResponseDto = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null);

                if (mapsResponseDto == null || mapsResponseDto.Result?.Data?.Items == null)
                {
                    throw new ApplicationException("Failed to retrieve maps from the repository");
                }

                {
                    logger.LogInformation($"Processing '{mapsResponseDto.Result.Data.Items.Count()}' maps for '{game.Key}'");

                    foreach (var mapDto in mapsResponseDto.Result.Data.Items)
                    {
                        var gameTrackerImageUrl = $"https://image.gametracker.com/images/maps/160x120/{game.Value}/{mapDto.MapName}.jpg";

                        try
                        {
                            using (var httpClient = new HttpClient())
                            {
                                // Setting TLS 1.2 via HttpClientHandler instead of ServicePointManager
                                var handler = new HttpClientHandler
                                {
                                    SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                };

                                using (var secureHttpClient = new HttpClient(handler))
                                {
                                    secureHttpClient.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/71.0.3578.98 Safari/537.36");

                                    var filePath = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
                                    using (var response = await secureHttpClient.GetAsync(gameTrackerImageUrl))
                                    {
                                        using (var fileStream = new FileStream(filePath, FileMode.Create))
                                        {
                                            await response.Content.CopyToAsync(fileStream);
                                        }
                                    }

                                    await repositoryApiClient.Maps.V1.UpdateMapImage(mapDto.MapId, filePath);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, $"Failed to retrieve map image from {gameTrackerImageUrl}", mapDto.TelemetryProperties);
                        }
                    }

                    skip += TakeEntries;
                    mapsResponseDto = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null);
                } while (mapsResponseDto != null && mapsResponseDto.Result != null && mapsResponseDto.Result.Data != null && mapsResponseDto.Result.Data.Items != null && mapsResponseDto.Result.Data.Items.Any()) ;
            }
        }
    }
}