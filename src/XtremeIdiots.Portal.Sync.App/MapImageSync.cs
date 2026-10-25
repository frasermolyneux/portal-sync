using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using XtremeIdiots.Portal.Sync.App.Configuration;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App;

public class MapImageSync(
    ILogger<MapImageSync> logger,
    IRepositoryApiClient repositoryApiClient,
    HttpClient httpClient,
    IOptions<MapImagesStorageOptions> mapImagesStorageOptions,
    IJobTelemetry jobTelemetry,
    IConfiguration configuration,
    IAuditLogger auditLogger)
{
    private const int TakeEntries = 50;
    private readonly string gameTrackerMapImageBaseUrl = (configuration["GameTracker:MapImageBaseUrl"] ?? "https://image.gametracker.com/images/maps/160x120/").TrimEnd('/') + "/";
    private readonly ILogger<MapImageSync> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IOptions<MapImagesStorageOptions> mapImagesStorageOptions = mapImagesStorageOptions ?? throw new ArgumentNullException(nameof(mapImagesStorageOptions));
    private readonly IJobTelemetry jobTelemetry = jobTelemetry ?? throw new ArgumentNullException(nameof(jobTelemetry));
    private readonly IAuditLogger auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

    [Function(nameof(RunMapImageSyncManual))]
    public async Task RunMapImageSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapImageSync(null).ConfigureAwait(false);
    }

    [Function(nameof(RunMapImageSync))]
    public async Task RunMapImageSync([TimerTrigger("0 0 0 * * 3")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(
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
            [GameType.CallOfDuty4x] = "cod4",
            [GameType.CallOfDuty5] = "codww",
            [GameType.UnrealTournament2004] = "ut2k4",
            [GameType.Insurgency] = "ins"
        };
        var validation = await repositoryApiClient.DataMaintenance.V1.ValidateMapImages().ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            throw new HttpRequestException("Unable to validate existing map images.");
        }
        var attempted = 0;
        var failures = new List<Exception>();

        foreach (var game in gamesToSync)
        {
            // Uploads remove maps from the filtered result, so enumerate before writing.
            var maps = new List<MapDto>();
            var identities = new HashSet<Guid>();
            while (true)
            {
                var response = await repositoryApiClient.Maps.V1.GetMaps(game.Key, null, MapsFilter.EmptyMapImage,
                    null, maps.Count, TakeEntries, null).ConfigureAwait(false);
                if (response?.IsSuccess != true || response.Result?.Data?.Items is null)
                {
                    throw new HttpRequestException("Unable to complete map image enumeration.");
                }
                var page = response.Result.Data.Items.ToArray();
                if (page.Length > TakeEntries || page.Any(map => map.MapId == Guid.Empty || !identities.Add(map.MapId)))
                {
                    throw new InvalidDataException("Invalid or repeated map identities prevent complete image enumeration.");
                }
                maps.AddRange(page);
                if (page.Length < TakeEntries)
                {
                    break;
                }
            }

            logger.LogInformation($"Processing '{maps.Count}' maps for '{game.Key}'");

            foreach (var mapDto in maps)
            {
                attempted++;
                var gameTrackerImageUrl = $"{gameTrackerMapImageBaseUrl}{game.Value}/{mapDto.MapName}.jpg";

                string? tempFilePath = null;
                try
                {
                    using var response = await httpClient.GetAsync(gameTrackerImageUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode != HttpStatusCode.NotFound)
                        {
                            response.EnsureSuccessStatusCode();
                        }
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
                    var update = await repositoryApiClient.Maps.V1.UpdateMapImage(mapDto.MapId, tempFilePath).ConfigureAwait(false);
                    if (!update.IsSuccess)
                    {
                        throw new HttpRequestException("Unable to persist the downloaded map image.");
                    }

                    auditLogger.LogAudit(AuditEvent.SystemAction("MapImageUploaded", AuditAction.Update)
                        .WithService("MapImageSync")
                        .WithProperty("GameType", game.Key.ToString())
                        .WithProperty("MapName", mapDto.MapName)
                        .WithSource("MapImageSync")
                        .Build());
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to retrieve map image from {gameTrackerImageUrl}", mapDto.TelemetryProperties);
                    failures.Add(ex);
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

        }
        if (failures.Count > 0)
        {
            throw new PartialJobFailureException(attempted, failures);
        }
    }
}
