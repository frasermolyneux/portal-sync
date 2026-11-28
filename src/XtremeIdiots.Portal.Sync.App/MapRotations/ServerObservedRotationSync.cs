using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public class ServerObservedRotationSync(
    ILogger<ServerObservedRotationSync> logger,
    IRepositoryApiClient repositoryApiClient,
    IServersApiClient serversApiClient,
    IJobTelemetry jobTelemetry)
{
    private static readonly GameType[] SupportedGames =
        [GameType.CallOfDuty2, GameType.CallOfDuty4, GameType.CallOfDuty4x, GameType.CallOfDuty5];

    [Function(nameof(RunServerObservedRotationSyncManual))]
    public Task RunServerObservedRotationSyncManual(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        CancellationToken cancellationToken = default) =>
        RunServerObservedRotationSync(null, cancellationToken);

    [Function(nameof(RunServerObservedRotationSync))]
    public Task RunServerObservedRotationSync([TimerTrigger("0 0 * * * *")] TimerInfo? timer,
        CancellationToken cancellationToken = default) =>
        jobTelemetry.ExecuteAsync(nameof(RunServerObservedRotationSync), () => CollectRotations(cancellationToken));

    private async Task CollectRotations(CancellationToken cancellationToken)
    {
        var servers = new List<GameServerDto>();
        var identities = new HashSet<Guid>();
        for (var skip = 0; ; skip += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await repositoryApiClient.GameServers.V1
                .GetGameServers(SupportedGames, null, null, skip, 100, null, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result?.Errors?.Length > 0 || response.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException($"Could not retrieve CoD server inventory: {response.StatusCode}.");
            }
            var page = items.ToArray();
            if (page.Length > 100 || page.Any(server => !identities.Add(server.GameServerId)))
            {
                throw new InvalidDataException("Repeated or oversized server pages prevent a complete observed-rotation inventory.");
            }
            servers.AddRange(page.Where(server => server.RconEnabled && !server.Deleted && SupportedGames.Contains(server.GameType)));
            if (page.Length < 100)
            {
                break;
            }
        }

        var failures = new List<Exception>();
        var attempted = 0;
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            using var scope = logger.BeginScope(server.TelemetryProperties);
            try
            {
                if (await HasManagedAssignment(server.GameServerId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // Timestamp the start of the read, not the delayed completion of a concurrent poll.
                var observedAt = DateTimeOffset.UtcNow;
                var response = await ReadRotation(server, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess || response.Result?.Errors?.Length > 0 || response.Result?.Data?.Items is not { } entries)
                {
                    throw new InvalidOperationException($"Could not read sv_mapRotation for server {server.GameServerId}: " +
                        $"{response.StatusCode}; {response.Result?.Errors?.FirstOrDefault()?.Message}. The previous snapshot is retained.");
                }
                var observation = new ObserveServerRotationDto
                {
                    ObservedAt = observedAt,
                    Entries = entries.Select(entry => new ObservedRotationEntryDto
                    {
                        MapName = entry.MapName,
                        GameMode = string.IsNullOrEmpty(entry.GameType) ? null : entry.GameType
                    }).ToList()
                };
                if (observation.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.MapName)))
                {
                    throw new InvalidDataException($"sv_mapRotation contained an invalid map name for server {server.GameServerId}. The previous snapshot is retained.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var persisted = await repositoryApiClient.MapRotations.V1
                    .ReplaceObservedServerRotation(server.GameServerId, observation, cancellationToken).ConfigureAwait(false);
                if (persisted.StatusCode == HttpStatusCode.Conflict && persisted.Result?.Errors is { Length: > 0 } errors
                    && errors.All(error => error.Code is "PORTAL_MANAGED_ASSIGNMENT_EXISTS" or "OBSERVATION_SUPERSEDED"))
                {
                    logger.LogInformation("Observation for server {GameServerId} was superseded by managed ownership or a newer poll.", server.GameServerId);
                    continue;
                }
                if (!persisted.IsSuccess || persisted.Result?.Errors?.Length > 0 || persisted.Result?.Data is not { } assignment
                    || assignment.Source != RotationSource.ServerObserved || assignment.GameServerId != server.GameServerId)
                {
                    throw new InvalidOperationException($"Could not persist observed rotation for server {server.GameServerId}: " +
                        $"{persisted.StatusCode}; {persisted.Result?.Errors?.FirstOrDefault()?.Message}.");
                }
                logger.LogInformation("Observed {EntryCount} rotation entries for server {GameServerId}.", observation.Entries.Count, server.GameServerId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                logger.LogError(ex, "Observed rotation collection failed for server {GameServerId}; continuing with other servers.", server.GameServerId);
            }
        }
        if (failures.Count > 0)
        {
            throw new PartialJobFailureException(attempted, failures);
        }
    }

    private async Task<bool> HasManagedAssignment(Guid serverId, CancellationToken cancellationToken)
    {
        var identities = new HashSet<Guid>();
        for (var skip = 0; ; skip += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await repositoryApiClient.MapRotations.V1
                .GetServerAssignments(null, serverId, null, skip, 100, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result?.Errors?.Length > 0 || response.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException($"Could not check managed rotation ownership for server {serverId}: {response.StatusCode}.");
            }
            var page = items.ToArray();
            if (page.Length > 100 || page.Any(assignment => !identities.Add(assignment.MapRotationServerAssignmentId)))
            {
                throw new InvalidDataException($"Repeated or oversized assignment pages prevent a complete ownership check for server {serverId}.");
            }
            if (page.Any(assignment => assignment.Source == RotationSource.PortalManaged
                && assignment.DeploymentState != DeploymentState.Removed && assignment.UnassignedAt is null))
            {
                return true;
            }
            if (page.Length < 100)
            {
                return false;
            }
        }
    }

    private Task<ApiResult<RconMapCollectionDto>> ReadRotation(GameServerDto server, CancellationToken cancellationToken) => server.GameType switch
    {
        GameType.CallOfDuty2 => serversApiClient.Cod2Rcon.V1.GetMaps(server.GameServerId, cancellationToken),
        GameType.CallOfDuty4 => serversApiClient.Cod4Rcon.V1.GetMaps(server.GameServerId, cancellationToken),
        GameType.CallOfDuty4x => serversApiClient.CoD4xRcon.V1.GetMaps(server.GameServerId, cancellationToken),
        GameType.CallOfDuty5 => serversApiClient.Cod5Rcon.V1.GetMaps(server.GameServerId, cancellationToken),
        _ => throw new InvalidOperationException($"Observed rotations are not supported for {server.GameType}.")
    };
}
