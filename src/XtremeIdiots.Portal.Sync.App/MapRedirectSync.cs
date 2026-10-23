using System.Diagnostics;
using System.Linq;

using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Models;
using XtremeIdiots.Portal.Sync.App.Redirect;

namespace XtremeIdiots.Portal.Sync.App;

public class MapRedirectSync(
    ILogger<MapRedirectSync> logger,
    IRepositoryApiClient repositoryApiClient,
    IMapRedirectRepository mapRedirectRepository,
    IJobTelemetry jobTelemetry,
    IAuditLogger auditLogger)
{
    public IRepositoryApiClient repositoryApiClient { get; } = repositoryApiClient;
    public IMapRedirectRepository MapRedirectRepository { get; } = mapRedirectRepository;
    [Function(nameof(RunMapRedirectSyncManual))]
    public async Task RunMapRedirectSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req, CancellationToken cancellationToken = default)
    {
        await RunMapRedirectSync(null, cancellationToken).ConfigureAwait(false);
    }

    [Function(nameof(RunMapRedirectSync))]
    // ReSharper disable once UnusedMember.Global
    public async Task RunMapRedirectSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer, CancellationToken cancellationToken = default)
    {
        await jobTelemetry.ExecuteAsync(
            nameof(RunMapRedirectSync),
            async () =>
            {
                logger.LogDebug("Start RunMapRedirectSync @ {Timestamp}", DateTime.UtcNow);

                Dictionary<GameType, string> gamesToSync = new()
                {
                    [GameType.CallOfDuty4] = "cod4",
                    [GameType.CallOfDuty4x] = "cod4",
                    [GameType.CallOfDuty5] = "cod5"
                };

                foreach (var group in gamesToSync.GroupBy(game => game.Value))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = await MapRedirectRepository.GetSnapshotForGame(group.Key, cancellationToken).ConfigureAwait(false);
                    foreach (var game in group)
                    {
                        await ProcessGameMapSync(game.Key, game.Value, snapshot, cancellationToken).ConfigureAwait(false);
                    }
                }

                logger.LogDebug("Stop RunMapRedirectSync @ {Timestamp}", DateTime.UtcNow);
            }).ConfigureAwait(false);
    }

    private async Task ProcessGameMapSync(GameType gameType, string gameKey, MapRedirectSnapshot snapshot, CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting map sync for game '{GameType}' with key '{GameKey}'", gameType, gameKey);

        try
        {
            var skipEntries = 0;
            var takeEntries = 500;

            List<MapDto> repositoryMaps = [];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mapsCollectionBatch = await repositoryApiClient.Maps.V1.GetMaps(gameType, null, null, null,
                    skipEntries, takeEntries, null, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!mapsCollectionBatch.IsSuccess || mapsCollectionBatch.Result?.Data?.Items is not { } items)
                {
                    throw new InvalidOperationException($"Repository map enumeration failed for '{gameType}' (HTTP {(int)mapsCollectionBatch.StatusCode}); no partial synchronization is permitted.");
                }
                if (!items.Any())
                {
                    break;
                }

                repositoryMaps.AddRange(items);
                skipEntries += takeEntries;
            }

            logger.LogInformation("Total maps retrieved from redirect for '{GameType}' is '{RedirectCount}' and repository is '{RepositoryCount}'",
                gameType, snapshot.Maps.Count, repositoryMaps.Count);

            // Compare the map entries in the redirect to those in the repository and generate a list of additions and changes.
            List<CreateMapDto> mapDtosToCreate = [];
            List<EditMapDto> mapDtosToUpdate = [];

            var redirectNames = snapshot.Maps.Select(map => map.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var manifest in snapshot.Maps)
            {
                var repositoryMap = repositoryMaps.SingleOrDefault(m => m.GameType == gameType && m.MapName == manifest.Name);
                var mapFiles = manifest.Files
                    .Where(file => file.Name.EndsWith(".iwd", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".ff", StringComparison.OrdinalIgnoreCase))
                    .Select(file => new MapFileDto(file.Path[(manifest.Path.Length + 1)..],
                        file.DirectUrl ?? throw new InvalidOperationException("Redirect manifest file has no direct URL."))
                    {
                        RedirectVersion = file.Version
                    })
                    .ToList();

                if (repositoryMap is null)
                {
                    var mapDtoToCreate = new CreateMapDto(gameType, manifest.Name)
                    {
                        MapFiles = mapFiles
                    };

                    mapDtosToCreate.Add(mapDtoToCreate);
                }
                else
                {
                    mapFiles.AddRange(GetExternalFiles(repositoryMap, snapshot.DirectBaseUrl, gameKey));
                    if (!mapFiles.OrderBy(file => file.FileName, StringComparer.Ordinal)
                        .ThenBy(file => file.Url, StringComparer.Ordinal)
                        .ThenBy(file => file.RedirectVersion, StringComparer.Ordinal)
                        .SequenceEqual(repositoryMap.MapFiles.OrderBy(file => file.FileName, StringComparer.Ordinal)
                            .ThenBy(file => file.Url, StringComparer.Ordinal)
                            .ThenBy(file => file.RedirectVersion, StringComparer.Ordinal)))
                    {
                        mapDtosToUpdate.Add(new EditMapDto(repositoryMap.MapId)
                        {
                            MapFiles = mapFiles,
                            ReplaceMapFiles = true
                        });
                    }
                }
            }

            foreach (var map in repositoryMaps.Where(map => !redirectNames.Contains(map.MapName)))
            {
                var retainedFiles = GetExternalFiles(map, snapshot.DirectBaseUrl, gameKey).ToList();
                if (retainedFiles.Count != map.MapFiles.Count)
                {
                    mapDtosToUpdate.Add(new EditMapDto(map.MapId) { MapFiles = retainedFiles, ReplaceMapFiles = true });
                }
            }

            logger.LogInformation("Creating {CreateCount} new maps and updating {UpdateCount} existing maps",
                mapDtosToCreate.Count, mapDtosToUpdate.Count);

            if (mapDtosToCreate.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var created = await repositoryApiClient.Maps.V1.CreateMaps(mapDtosToCreate, cancellationToken).ConfigureAwait(false);
                if (!created.IsSuccess)
                {
                    throw new InvalidOperationException($"Creating maps from redirect failed (HTTP {(int)created.StatusCode}).");
                }

                foreach (var mapDto in mapDtosToCreate)
                {
                    auditLogger.LogAudit(AuditEvent.SystemAction("MapCreatedFromRedirect", AuditAction.Create)
                        .WithService("MapRedirectSync")
                        .WithProperty("GameType", gameType.ToString())
                        .WithProperty("MapName", mapDto.MapName)
                        .WithSource("MapRedirectSync")
                        .Build());
                }
            }

            if (mapDtosToUpdate.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var updated = await repositoryApiClient.Maps.V1.UpdateMaps(mapDtosToUpdate, cancellationToken).ConfigureAwait(false);
                if (!updated.IsSuccess)
                {
                    throw new InvalidOperationException($"Updating maps from redirect failed (HTTP {(int)updated.StatusCode}).");
                }

                foreach (var mapDto in mapDtosToUpdate)
                {
                    auditLogger.LogAudit(AuditEvent.SystemAction("MapUpdatedFromRedirect", AuditAction.Update)
                        .WithService("MapRedirectSync")
                        .WithProperty("GameType", gameType.ToString())
                        .WithProperty("MapId", mapDto.MapId.ToString())
                        .WithSource("MapRedirectSync")
                        .Build());
                }
            }

            logger.LogInformation("Completed map sync for game '{GameType}'", gameType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to sync maps for game '{GameType}' with key '{GameKey}'", gameType, gameKey);
            throw;
        }
    }

    private static IEnumerable<MapFileDto> GetExternalFiles(MapDto map, string directBaseUrl, string gameKey)
    {
        var mapRoot = new Uri($"{directBaseUrl.TrimEnd('/')}/redirect/{gameKey}/usermaps/{Uri.EscapeDataString(map.MapName)}/");
        return map.MapFiles.Where(file =>
            !Uri.TryCreate(file.Url, UriKind.Absolute, out var fileUrl) || !mapRoot.IsBaseOf(fileUrl));
    }
}
