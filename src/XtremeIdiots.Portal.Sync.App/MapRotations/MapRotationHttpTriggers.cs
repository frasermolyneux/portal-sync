using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

[Authorize(Roles = "ServiceAccount")]
public class MapRotationHttpTriggers(ILogger<MapRotationHttpTriggers> logger)
{
    private readonly ILogger<MapRotationHttpTriggers> logger = logger ?? throw new ArgumentNullException(nameof(logger));

    [Function(nameof(TriggerSyncMapRotation))]
    public async Task<HttpResponseData> TriggerSyncMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/sync/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-sync-{assignmentId}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("SyncMapRotation already running for assignment {AssignmentId}, instance {InstanceId}", assignmentId, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        // Purge any previous terminal instance so the ID can be reused
        if (existing is not null)
        {
            logger.LogInformation("Purging previous orchestration {InstanceId} in state {RuntimeStatus}", instanceId, existing.RuntimeStatus);
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.SyncMapRotationOrchestrator),
            new SyncOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started SyncMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(TriggerRemoveMapRotation))]
    public async Task<HttpResponseData> TriggerRemoveMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/remove/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-remove-{assignmentId}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("RemoveMapRotation already running for assignment {AssignmentId}, instance {InstanceId}", assignmentId, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            logger.LogInformation("Purging previous orchestration {InstanceId} in state {RuntimeStatus}", instanceId, existing.RuntimeStatus);
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.RemoveMapRotationOrchestrator),
            new RemoveOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started RemoveMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(TriggerActivateMapRotation))]
    public async Task<HttpResponseData> TriggerActivateMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/activate/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-activate-{assignmentId}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("ActivateMapRotation already running for assignment {AssignmentId}, instance {InstanceId}", assignmentId, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            logger.LogInformation("Purging previous orchestration {InstanceId} in state {RuntimeStatus}", instanceId, existing.RuntimeStatus);
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.ActivateMapRotationOrchestrator),
            new ActivateOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started ActivateMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(TriggerDeactivateMapRotation))]
    public async Task<HttpResponseData> TriggerDeactivateMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/deactivate/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-deactivate-{assignmentId}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("DeactivateMapRotation already running for assignment {AssignmentId}, instance {InstanceId}", assignmentId, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            logger.LogInformation("Purging previous orchestration {InstanceId} in state {RuntimeStatus}", instanceId, existing.RuntimeStatus);
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.DeactivateMapRotationOrchestrator),
            new DeactivateOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started DeactivateMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(TriggerVerifyMapRotation))]
    public async Task<HttpResponseData> TriggerVerifyMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/verify/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-verify-{assignmentId}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("VerifyMapRotation already running for assignment {AssignmentId}, instance {InstanceId}", assignmentId, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            logger.LogInformation("Purging previous orchestration {InstanceId} in state {RuntimeStatus}", instanceId, existing.RuntimeStatus);
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.VerifyMapRotationOrchestrator),
            new VerifyOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started VerifyMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(GetMapRotationOrchestrationStatus))]
    public async Task<HttpResponseData> GetMapRotationOrchestrationStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "map-rotations/status/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        var metadata = await client.GetInstanceAsync(instanceId, getInputsAndOutputs: true).ConfigureAwait(false);

        if (metadata is null)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { error = "Orchestration instance not found", instanceId }).ConfigureAwait(false);
            return notFoundResponse;
        }

        // Parse custom status from serialized JSON to avoid double-encoding
        JsonNode? customStatus = null;
        if (!string.IsNullOrEmpty(metadata.SerializedCustomStatus))
        {
            try { customStatus = JsonNode.Parse(metadata.SerializedCustomStatus); }
            catch { /* ignore parse failures */ }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId = metadata.InstanceId,
            runtimeStatus = metadata.RuntimeStatus.ToString(),
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            progress = customStatus
        }).ConfigureAwait(false);

        return response;
    }

    [Function(nameof(TerminateMapRotationOrchestration))]
    public async Task<HttpResponseData> TerminateMapRotationOrchestration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/terminate/{instanceId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        string instanceId)
    {
        var metadata = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);

        if (metadata is null)
        {
            var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
            await notFoundResponse.WriteAsJsonAsync(new { error = "Orchestration instance not found", instanceId }).ConfigureAwait(false);
            return notFoundResponse;
        }

        if (metadata.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            await client.TerminateInstanceAsync(instanceId, "Manually cancelled by user via portal").ConfigureAwait(false);
            logger.LogInformation("Terminated orchestration {InstanceId} (was {RuntimeStatus})", instanceId, metadata.RuntimeStatus);
        }
        else
        {
            logger.LogInformation("Orchestration {InstanceId} already in terminal state {RuntimeStatus}, no termination needed", instanceId, metadata.RuntimeStatus);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { instanceId, terminated = true, previousStatus = metadata.RuntimeStatus.ToString() }).ConfigureAwait(false);
        return response;
    }

    [Function(nameof(TriggerPushMapToServer))]
    public async Task<HttpResponseData> TriggerPushMapToServer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "maps/push/{gameServerId}/{gameType}/{mapName}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid gameServerId,
        string gameType,
        string mapName)
    {
        if (!Enum.TryParse<XtremeIdiots.Portal.Repository.Abstractions.Constants.V1.GameType>(gameType, true, out var parsedGameType))
        {
            var badGameTypeResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badGameTypeResponse.WriteStringAsync($"Invalid game type: {gameType}").ConfigureAwait(false);
            return badGameTypeResponse;
        }

        var safeMapName = new string(mapName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
        if (string.IsNullOrEmpty(safeMapName))
        {
            var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await badResponse.WriteStringAsync("Map name must contain at least one alphanumeric character").ConfigureAwait(false);
            return badResponse;
        }
        var instanceId = $"map-push-{gameServerId}-{safeMapName}";

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && existing.RuntimeStatus is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending)
        {
            logger.LogWarning("PushMapToServer already running for {GameServerId}/{MapName}, instance {InstanceId}", gameServerId, mapName, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.PushMapToServerOrchestrator),
            new PushMapOrchestrationInput(gameServerId, mapName, parsedGameType),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started PushMapToServer orchestration {InstanceId} for {GameServerId}/{MapName}", instanceId, gameServerId, mapName);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }
}
