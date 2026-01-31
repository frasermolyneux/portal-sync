using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;
using XtremeIdiots.Portal.Sync.App.Configuration;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App;

public class MapImageSync(
    ILogger<MapImageSync> logger,
    IRepositoryApiClient repositoryApiClient,
    HttpClient httpClient,
    IOptions<MapImagesStorageOptions> mapImagesStorageOptions,
    TelemetryClient telemetryClient)
{
    private const int TakeEntries = 50;
    // Single attempt; retry & bot-avoidance logic removed.
    private readonly ILogger<MapImageSync> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IOptions<MapImagesStorageOptions> mapImagesStorageOptions = mapImagesStorageOptions ?? throw new ArgumentNullException(nameof(mapImagesStorageOptions));
    private readonly TelemetryClient telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
    // Note: HTML detection maintained to avoid storing block/anti-bot pages as images.

    [Function(nameof(RunMapImageSyncManual))]
    public async Task RunMapImageSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapImageSync(null).ConfigureAwait(false);
    }

    [Function(nameof(RunMapImageSync))]
    public async Task RunMapImageSync([TimerTrigger("0 0 0 * * 3")] TimerInfo? myTimer)
    {
        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            telemetryClient,
            nameof(RunMapImageSync),
            async () =>
            {
                await ProcessMapImages().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task ProcessMapImages()
    {
        Dictionary<GameType, string> gamesToSync = new()
        {
            [GameType.CallOfDuty2] = "cod2",
            [GameType.CallOfDuty4] = "cod4",
            [GameType.CallOfDuty5] = "codww",
            [GameType.UnrealTournament2004] = "ut2k4",
            [GameType.Insurgency] = "ins"
        };
        await repositoryApiClient.DataMaintenance.V1.ValidateMapImages().ConfigureAwait(false);

        foreach (var game in gamesToSync)
        {
            var skip = 0;
            var mapsResponseDto = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null).ConfigureAwait(false);

            if (mapsResponseDto == null || !mapsResponseDto.IsSuccess || mapsResponseDto.Result?.Data?.Items == null)
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
                        using var response = await httpClient.GetAsync(gameTrackerImageUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.LogDebug("Skipping map image for {MapName} - status code {StatusCode}", mapDto.MapName, response.StatusCode);
                            continue;
                        }

                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
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
                        await File.WriteAllBytesAsync(tempFilePath, bytes).ConfigureAwait(false);
                        await repositoryApiClient.Maps.V1.UpdateMapImage(mapDto.MapId, tempFilePath).ConfigureAwait(false);
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
                mapsResponseDto = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage, null, skip, TakeEntries, null).ConfigureAwait(false);
            } while (mapsResponseDto != null && mapsResponseDto.IsSuccess && mapsResponseDto.Result != null && mapsResponseDto.Result.Data != null && mapsResponseDto.Result.Data.Items != null && mapsResponseDto.Result.Data.Items.Any()) ;
        }
    }
}