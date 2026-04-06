using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

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

        await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(MapRotationOrchestrators.DeactivateMapRotationOrchestrator),
            new DeactivateOrchestrationInput(assignmentId),
            new StartOrchestrationOptions { InstanceId = instanceId }).ConfigureAwait(false);

        logger.LogInformation("Started DeactivateMapRotation orchestration {InstanceId} for assignment {AssignmentId}", instanceId, assignmentId);
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
}
