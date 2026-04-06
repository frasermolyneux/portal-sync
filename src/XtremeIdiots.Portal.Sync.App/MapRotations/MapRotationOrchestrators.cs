using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public static class MapRotationOrchestrators
{
    [Function(nameof(SyncMapRotationOrchestrator))]
    public static async Task SyncMapRotationOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<SyncOrchestrationInput>()
            ?? throw new InvalidOperationException("SyncOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(SyncMapRotationOrchestrator));
        var instanceId = context.InstanceId;

        // Record operation
        var operationId = await context.CallActivityAsync<Guid>(
            nameof(MapRotationActivities.RecordOperation),
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Sync, instanceId)).ConfigureAwait(false);

        try
        {
            // Update assignment status to Syncing
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, DeploymentState: DeploymentState.Syncing)).ConfigureAwait(false);

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId)).ConfigureAwait(false);

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds)).ConfigureAwait(false);

            if (mapNames.Count == 0)
            {
                throw new InvalidOperationException("No maps resolved for rotation");
            }

            // Push each map sequentially to avoid FTP overload
            var failures = new List<string>();
            foreach (var mapName in mapNames)
            {
                var result = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SyncSingleMapToServer),
                    new SyncMapInput(details.GameServerId, mapName)).ConfigureAwait(false);

                if (!result.Success)
                {
                    failures.Add($"{result.MapName}: {result.Error}");
                    logger.LogWarning("Failed to sync map {MapName}: {Error}", result.MapName, result.Error);
                }
            }

            if (failures.Count > 0)
            {
                var errorMessage = $"Failed to sync {failures.Count}/{mapNames.Count} maps: {string.Join("; ", failures)}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage)).ConfigureAwait(false);

                return;
            }

            // Success — update assignment to Synced with version
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Synced,
                    DeployedVersion: details.RotationVersion)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message)).ConfigureAwait(false);
        }
    }

    [Function(nameof(RemoveMapRotationOrchestrator))]
    public static async Task RemoveMapRotationOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<RemoveOrchestrationInput>()
            ?? throw new InvalidOperationException("RemoveOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(RemoveMapRotationOrchestrator));
        var instanceId = context.InstanceId;

        var operationId = await context.CallActivityAsync<Guid>(
            nameof(MapRotationActivities.RecordOperation),
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Remove, instanceId)).ConfigureAwait(false);

        try
        {
            // Update assignment status to Removing
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, DeploymentState: DeploymentState.Removing)).ConfigureAwait(false);

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId)).ConfigureAwait(false);

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds)).ConfigureAwait(false);

            // Get maps that are still needed by other active rotations on the same server
            var sharedMapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.GetMapsInOtherActiveRotations),
                new GetSharedMapsInput(details.GameServerId, input.AssignmentId)).ConfigureAwait(false);

            // Only remove maps that are NOT used by other active rotations
            var safeToRemove = mapNames
                .Where(m => !sharedMapNames.Contains(m, StringComparer.OrdinalIgnoreCase))
                .ToList();

            logger.LogInformation(
                "Removing {SafeCount} maps (skipping {SharedCount} shared maps) for assignment {AssignmentId}",
                safeToRemove.Count, mapNames.Count - safeToRemove.Count, input.AssignmentId);

            // Remove each safe map sequentially
            var failures = new List<string>();
            foreach (var mapName in safeToRemove)
            {
                var result = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.RemoveSingleMapFromServer),
                    new RemoveMapInput(details.GameServerId, mapName)).ConfigureAwait(false);

                if (!result.Success)
                {
                    failures.Add($"{result.MapName}: {result.Error}");
                    logger.LogWarning("Failed to remove map {MapName}: {Error}", result.MapName, result.Error);
                }
            }

            if (failures.Count > 0)
            {
                var errorMessage = $"Failed to remove {failures.Count}/{safeToRemove.Count} maps: {string.Join("; ", failures)}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage)).ConfigureAwait(false);

                return;
            }

            // Update assignment to Removed
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Removed,
                    UnassignedAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RemoveMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message)).ConfigureAwait(false);
        }
    }

    [Function(nameof(ActivateMapRotationOrchestrator))]
    public static async Task ActivateMapRotationOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<ActivateOrchestrationInput>()
            ?? throw new InvalidOperationException("ActivateOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(ActivateMapRotationOrchestrator));
        var instanceId = context.InstanceId;

        var operationId = await context.CallActivityAsync<Guid>(
            nameof(MapRotationActivities.RecordOperation),
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Activate, instanceId)).ConfigureAwait(false);

        try
        {
            // Update activation state to Activating
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Activating)).ConfigureAwait(false);

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId)).ConfigureAwait(false);

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds)).ConfigureAwait(false);

            if (mapNames.Count == 0)
            {
                throw new InvalidOperationException("No maps resolved for rotation activation");
            }

            // Format the rotation string
            var rotationString = await context.CallActivityAsync<string>(
                nameof(MapRotationActivities.FormatRotationString),
                new FormatRotationInput(mapNames, details.GameMode ?? "war", details.ConfigVariableName ?? "sv_maprotation")).ConfigureAwait(false);

            // Write config variable
            var configResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.WriteConfigVariable),
                new WriteConfigInput(
                    details.GameServerId,
                    details.ConfigFilePath ?? "",
                    details.ConfigVariableName ?? "sv_maprotation",
                    rotationString)).ConfigureAwait(false);

            // Set RCON dvar
            var rconResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.SetRconDvar),
                new SetRconDvarInput(
                    details.GameServerId,
                    details.ConfigVariableName ?? "sv_maprotation",
                    rotationString)).ConfigureAwait(false);

            // Both must succeed for activation to be considered complete
            if (!configResult.Success || !rconResult.Success)
            {
                var errors = new List<string>();
                if (!configResult.Success) errors.Add($"Config write failed: {configResult.Error}");
                if (!rconResult.Success) errors.Add($"RCON set failed: {rconResult.Error}");
                var errorMessage = string.Join("; ", errors);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage)).ConfigureAwait(false);

                return;
            }

            // Update activation state to Active with version
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Active,
                    ActivatedVersion: details.RotationVersion)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ActivateMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message)).ConfigureAwait(false);
        }
    }

    [Function(nameof(DeactivateMapRotationOrchestrator))]
    public static async Task DeactivateMapRotationOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<DeactivateOrchestrationInput>()
            ?? throw new InvalidOperationException("DeactivateOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(DeactivateMapRotationOrchestrator));
        var instanceId = context.InstanceId;

        var operationId = await context.CallActivityAsync<Guid>(
            nameof(MapRotationActivities.RecordOperation),
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Deactivate, instanceId)).ConfigureAwait(false);

        try
        {
            // Update activation state to Deactivating
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Deactivating)).ConfigureAwait(false);

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId)).ConfigureAwait(false);

            // Write empty value to config variable
            var configResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.WriteConfigVariable),
                new WriteConfigInput(
                    details.GameServerId,
                    details.ConfigFilePath ?? "",
                    details.ConfigVariableName ?? "sv_maprotation",
                    "")).ConfigureAwait(false);

            // Set RCON dvar to empty
            var rconResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.SetRconDvar),
                new SetRconDvarInput(
                    details.GameServerId,
                    details.ConfigVariableName ?? "sv_maprotation",
                    "")).ConfigureAwait(false);

            // Both must succeed for deactivation to be considered complete
            if (!configResult.Success || !rconResult.Success)
            {
                var errors = new List<string>();
                if (!configResult.Success) errors.Add($"Config write failed: {configResult.Error}");
                if (!rconResult.Success) errors.Add($"RCON set failed: {rconResult.Error}");
                var errorMessage = string.Join("; ", errors);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage)).ConfigureAwait(false);

                return;
            }

            // Update activation state to Inactive
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Inactive)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeactivateMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime)).ConfigureAwait(false);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message)).ConfigureAwait(false);
        }
    }
}
