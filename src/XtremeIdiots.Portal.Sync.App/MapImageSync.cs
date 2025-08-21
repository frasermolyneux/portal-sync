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

            // After processing, perform cleanup of any HTML placeholder blobs already in storage.
            await CleanupHtmlPlaceholderImages();
        }

        [Function(nameof(RunMapImagesHtmlCleanupManual))]
        public async Task RunMapImagesHtmlCleanupManual([HttpTrigger(AuthorizationLevel.Function, "post", Route = "map-images/cleanup-html")] HttpRequest req)
        {
            await CleanupHtmlPlaceholderImages();
        }

        private async Task CleanupHtmlPlaceholderImages()
        {
            const string targetMd5Base64 = "aJ39V4B09SgLKGEgsVkteA=="; // Known MD5 of HTML placeholder
            byte[] targetHash;
            try
            {
                targetHash = Convert.FromBase64String(targetMd5Base64);
            }
            catch (FormatException)
            {
                logger.LogWarning("Configured target MD5 is invalid base64; skipping HTML cleanup");
                return;
            }

            var endpoint = mapImagesStorageOptions.Value.StorageBlobEndpoint;
            var containerName = mapImagesStorageOptions.Value.ContainerName;

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(containerName))
            {
                logger.LogDebug("Map image storage options not configured; skipping HTML cleanup");
                return;
            }

            try
            {
                var blobServiceClient = new BlobServiceClient(new Uri(endpoint), new DefaultAzureCredential());
                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

                if (!await containerClient.ExistsAsync())
                {
                    logger.LogWarning("Map image container {Container} does not exist at {Endpoint}", containerName, endpoint);
                    return;
                }

                var toDelete = new List<string>();
                await foreach (var blob in containerClient.GetBlobsAsync())
                {
                    var hash = blob.Properties.ContentHash;
                    if (hash == null || hash.Length == 0) continue;
                    if (hash.Length == targetHash.Length && hash.SequenceEqual(targetHash))
                    {
                        toDelete.Add(blob.Name);
                    }
                }

                if (!toDelete.Any())
                {
                    logger.LogInformation("No HTML placeholder map images found for deletion.");
                    return;
                }

                logger.LogInformation("Deleting {Count} HTML placeholder map image blobs", toDelete.Count);
                foreach (var blobName in toDelete)
                {
                    try
                    {
                        await containerClient.DeleteBlobIfExistsAsync(blobName);
                        logger.LogDebug("Deleted placeholder blob {BlobName}", blobName);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to delete placeholder blob {BlobName}", blobName);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed during HTML placeholder cleanup for map images");
            }
        }
    }
}