using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using XtremeIdiots.Portal.Sync.App.Configuration;

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
        // Single attempt; retry & bot-avoidance logic removed.
        private readonly ILogger<MapImageSync> logger;
        private readonly IRepositoryApiClient repositoryApiClient;
        private readonly HttpClient httpClient;
        private readonly IOptions<MapImagesStorageOptions> mapImagesStorageOptions;

        // Note: HTML detection maintained to avoid storing block/anti-bot pages as images.

        public MapImageSync(
            ILogger<MapImageSync> logger,
            IRepositoryApiClient repositoryApiClient,
            HttpClient httpClient,
            IOptions<MapImagesStorageOptions> mapImagesStorageOptions)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            this.mapImagesStorageOptions = mapImagesStorageOptions ?? throw new ArgumentNullException(nameof(mapImagesStorageOptions));
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

            await repositoryApiClient.DataMaintenance.V1.ValidateMapImages();

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

                        string? tempFilePath = null;
                        try
                        {
                            using var response = await httpClient.GetAsync(gameTrackerImageUrl, HttpCompletionOption.ResponseHeadersRead);
                            if (!response.IsSuccessStatusCode)
                            {
                                logger.LogDebug("Skipping map image for {MapName} - status code {StatusCode}", mapDto.MapName, response.StatusCode);
                                continue;
                            }

                            var contentType = response.Content.Headers.ContentType?.MediaType;
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            if (bytes.Length == 0)
                            {
                                logger.LogDebug("Empty response for {MapName}", mapDto.MapName);
                                continue;
                            }

                            bool looksLikeHtml = false;
                            if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                            {
                                looksLikeHtml = true;
                            }
                            else
                            {
                                var sampleLength = Math.Min(256, bytes.Length);
                                var prefix = Encoding.UTF8.GetString(bytes, 0, sampleLength).TrimStart();
                                if (prefix.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) || prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                                {
                                    looksLikeHtml = true;
                                }
                            }

                            if (looksLikeHtml)
                            {
                                logger.LogDebug("HTML page detected instead of image for {MapName}; skipping.", mapDto.MapName);
                                continue;
                            }

                            if (!string.IsNullOrEmpty(contentType) && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                            {
                                logger.LogDebug("Non-image content-type {ContentType} for {MapName}; skipping", contentType, mapDto.MapName);
                                continue;
                            }

                            tempFilePath = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
                            await File.WriteAllBytesAsync(tempFilePath, bytes);
                            await repositoryApiClient.Maps.V1.UpdateMapImage(mapDto.MapId, tempFilePath);
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, $"Failed to retrieve map image from {gameTrackerImageUrl}", mapDto.TelemetryProperties);
                        }
                        finally
                        {
                            if (!string.IsNullOrEmpty(tempFilePath))
                            {
                                try
                                {
                                    if (File.Exists(tempFilePath))
                                    {
                                        File.Delete(tempFilePath);
                                    }
                                }
                                catch (Exception cleanupEx)
                                {
                                    logger.LogDebug(cleanupEx, "Failed to delete temp file {TempFilePath}", tempFilePath);
                                }
                            }
                        }
                    }

                    skip += TakeEntries;
                    mapsResponseDto = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null);
                } while (mapsResponseDto != null && mapsResponseDto.Result != null && mapsResponseDto.Result.Data != null && mapsResponseDto.Result.Data.Items != null && mapsResponseDto.Result.Data.Items.Any()) ;
            }
        }
    }
}