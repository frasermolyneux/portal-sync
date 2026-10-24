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

    [Function(nameof(TriggerApplyMapRotation))]
    public async Task<HttpResponseData> TriggerApplyMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/apply/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-apply-{assignmentId}";
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null)
        {
            return conflict;
        }
        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
        {
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }
        if (existing is not null)
        {
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }
        await client.ScheduleNewOrchestrationInstanceAsync(nameof(MapRotationWorkflow.ApplyMapRotationV2),
            new SyncOrchestrationInput(assignmentId), new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    [Function(nameof(TriggerSyncMapRotation))]
    public async Task<HttpResponseData> TriggerSyncMapRotation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "map-rotations/sync/{assignmentId}")] HttpRequestData req,
        [DurableClient] DurableTaskClient client,
        Guid assignmentId)
    {
        var instanceId = $"maprot-sync-{assignmentId}";
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null) { return conflict; }

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
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
            nameof(MapRotationWorkflow.RepairMapRotationV2),
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
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null) { return conflict; }

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
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
            nameof(MapRotationWorkflow.UnassignMapRotationV2),
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
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null) { return conflict; }

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
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
            nameof(MapRotationWorkflow.ActivateMapRotationV2),
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
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null) { return conflict; }

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
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
            nameof(MapRotationWorkflow.DeactivateMapRotationV2),
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
        var conflict = await RejectConflictingOperation(req, client, assignmentId, instanceId).ConfigureAwait(false);
        if (conflict != null) { return conflict; }

        var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
        if (existing is not null && IsBusy(existing.RuntimeStatus))
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
            nameof(MapRotationWorkflow.VerifyMapRotationV2),
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

        var customStatus = ParseState(metadata.SerializedCustomStatus, instanceId);
        var result = ParseState(metadata.SerializedOutput, instanceId) as JsonObject;
        var successValue = result?["Success"] ?? result?["success"];
        bool? success = successValue is JsonValue value && value.TryGetValue<bool>(out var succeeded) ? succeeded : null;

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            instanceId = metadata.InstanceId,
            runtimeStatus = metadata.RuntimeStatus.ToString(),
            createdAt = metadata.CreatedAt,
            lastUpdatedAt = metadata.LastUpdatedAt,
            progress = customStatus,
            success,
            result
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

        if (IsBusy(metadata.RuntimeStatus))
        {
            if (metadata.Name.EndsWith("V2", StringComparison.Ordinal))
            {
                await client.RaiseEventAsync(instanceId, MapRotationWorkflow.CancellationEvent, new { requested = true }).ConfigureAwait(false);
                if (metadata.RuntimeStatus == OrchestrationRuntimeStatus.Suspended)
                {
                    await client.ResumeInstanceAsync(instanceId, "Resume only to process the requested safe cancellation.").ConfigureAwait(false);
                }
                var accepted = req.CreateResponse(HttpStatusCode.Accepted);
                await accepted.WriteAsJsonAsync(new { instanceId, terminated = false, cancellationRequested = true }).ConfigureAwait(false);
                accepted.StatusCode = HttpStatusCode.Accepted;
                return accepted;
            }
            await client.TerminateInstanceAsync(instanceId, "Manually cancelled by user via portal").ConfigureAwait(false);
            logger.LogInformation("Requested termination of legacy orchestration {InstanceId} (was {RuntimeStatus})", instanceId, metadata.RuntimeStatus);
            var acceptedLegacy = req.CreateResponse(HttpStatusCode.Accepted);
            await acceptedLegacy.WriteAsJsonAsync(new
            {
                instanceId,
                terminated = false,
                cancellationRequested = true,
                warning = "An in-flight legacy activity may still finish. Verify files and configuration before retrying."
            }).ConfigureAwait(false);
            acceptedLegacy.StatusCode = HttpStatusCode.Accepted;
            return acceptedLegacy;
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
        if (!Enum.TryParse<Portal.Repository.Abstractions.Constants.V1.GameType>(gameType, true, out var parsedGameType))
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
        if (existing is not null && IsBusy(existing.RuntimeStatus))
        {
            logger.LogWarning("PushMapToServer already running for {GameServerId}/{MapName}, instance {InstanceId}", gameServerId, mapName, instanceId);
            return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
        }

        if (existing is not null)
        {
            await client.PurgeInstanceAsync(instanceId).ConfigureAwait(false);
        }

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationWorkflow.PushMapToServerV2),
            new PushMapOrchestrationInput(gameServerId, mapName, parsedGameType),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started PushMapToServer orchestration {InstanceId} for {GameServerId}/{MapName}", instanceId, gameServerId, mapName);
        return await client.CreateCheckStatusResponseAsync(req, instanceId).ConfigureAwait(false);
    }

    private JsonNode? ParseState(string? serialized, string instanceId)
    {
        if (string.IsNullOrEmpty(serialized))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(serialized);
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger.LogWarning(ex, "Invalid state JSON for orchestration {InstanceId}", instanceId);
            return null;
        }
    }

    private static bool IsBusy(OrchestrationRuntimeStatus state) =>
        state is OrchestrationRuntimeStatus.Running or OrchestrationRuntimeStatus.Pending or OrchestrationRuntimeStatus.Suspended;

    private static async Task<HttpResponseData?> RejectConflictingOperation(
        HttpRequestData req, DurableTaskClient client, Guid assignmentId, string requestedId)
    {
        foreach (var action in new[] { "sync", "apply", "activate", "deactivate", "verify", "remove" })
        {
            var instanceId = $"maprot-{action}-{assignmentId}";
            if (instanceId == requestedId)
            {
                continue;
            }
            var existing = await client.GetInstanceAsync(instanceId).ConfigureAwait(false);
            if (existing is not null && IsBusy(existing.RuntimeStatus))
            {
                var response = req.CreateResponse(HttpStatusCode.Conflict);
                await response.WriteAsJsonAsync(new { error = "Another operation is already in progress for this assignment.", instanceId }).ConfigureAwait(false);
                return response;
            }
        }
        return null;
    }
}
