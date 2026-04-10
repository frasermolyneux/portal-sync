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
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Sync, instanceId));

        try
        {
            // Update assignment status to Syncing
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, DeploymentState: DeploymentState.Syncing));

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId));

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds));

            if (mapNames.Count == 0)
            {
                throw new InvalidOperationException("No maps resolved for rotation");
            }

            // Initialize progress tracking
            var mapProgress = mapNames.Select(m => new MapProgress(m, "Pending")).ToList();
            context.SetCustomStatus(new OrchestrationProgress("Sync", mapNames.Count, 0, mapProgress));

            // Push each map sequentially to avoid FTP overload
            var failures = new List<string>();
            for (var i = 0; i < mapNames.Count; i++)
            {
                var mapName = mapNames[i];
                mapProgress[i] = mapProgress[i] with { Status = "InProgress" };
                context.SetCustomStatus(new OrchestrationProgress("Sync", mapNames.Count, i, mapProgress));

                try
                {
                    var result = await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.SyncSingleMapToServer),
                        new SyncMapInput(details.GameServerId, mapName));

                    if (result.Success)
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Completed" };
                    }
                    else
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Failed", Error = result.Error };
                        failures.Add($"{result.MapName}: {result.Error}");
                        logger.LogWarning("Failed to sync map {MapName}: {Error}", result.MapName, result.Error);
                    }
                }
                catch (Exception mapEx)
                {
                    mapProgress[i] = mapProgress[i] with { Status = "Failed", Error = mapEx.Message };
                    failures.Add($"{mapName}: {mapEx.Message}");
                    logger.LogWarning(mapEx, "Exception syncing map {MapName}", mapName);
                }

                context.SetCustomStatus(new OrchestrationProgress("Sync", mapNames.Count, i + 1, mapProgress));
            }

            if (failures.Count > 0)
            {
                var errorMessage = $"Failed to sync {failures.Count}/{mapNames.Count} maps: {string.Join("; ", failures)}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage));

                return;
            }

            // Success — update assignment to Synced with version, clear any previous error
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Synced,
                    DeployedVersion: details.RotationVersion,
                    LastError: "",
                    LastErrorAt: null));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SyncMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
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
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Remove, instanceId));

        try
        {
            // Update assignment status to Removing
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, DeploymentState: DeploymentState.Removing));

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId));

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds));

            // Get maps that are still needed by other active rotations on the same server
            var sharedMapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.GetMapsInOtherActiveRotations),
                new GetSharedMapsInput(details.GameServerId, input.AssignmentId));

            // Only remove maps that are NOT used by other active rotations
            var safeToRemove = mapNames
                .Where(m => !sharedMapNames.Contains(m, StringComparer.OrdinalIgnoreCase))
                .ToList();

            logger.LogInformation(
                "Removing {SafeCount} maps (skipping {SharedCount} shared maps) for assignment {AssignmentId}",
                safeToRemove.Count, mapNames.Count - safeToRemove.Count, input.AssignmentId);

            // Initialize progress tracking — include all maps, mark shared ones as Skipped
            var mapProgress = mapNames.Select(m =>
                sharedMapNames.Contains(m, StringComparer.OrdinalIgnoreCase)
                    ? new MapProgress(m, "Skipped")
                    : new MapProgress(m, "Pending")).ToList();
            var totalToProcess = safeToRemove.Count;
            context.SetCustomStatus(new OrchestrationProgress("Remove", totalToProcess, 0, mapProgress));

            // Remove each safe map sequentially
            var failures = new List<string>();
            var processed = 0;
            for (var i = 0; i < mapNames.Count; i++)
            {
                if (mapProgress[i].Status == "Skipped")
                    continue;

                var mapName = mapNames[i];
                mapProgress[i] = mapProgress[i] with { Status = "InProgress" };
                context.SetCustomStatus(new OrchestrationProgress("Remove", totalToProcess, processed, mapProgress));

                try
                {
                    var result = await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.RemoveSingleMapFromServer),
                        new RemoveMapInput(details.GameServerId, mapName));

                    if (result.Success)
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Completed" };
                    }
                    else
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Failed", Error = result.Error };
                        failures.Add($"{result.MapName}: {result.Error}");
                        logger.LogWarning("Failed to remove map {MapName}: {Error}", result.MapName, result.Error);
                    }
                }
                catch (Exception mapEx)
                {
                    mapProgress[i] = mapProgress[i] with { Status = "Failed", Error = mapEx.Message };
                    failures.Add($"{mapName}: {mapEx.Message}");
                    logger.LogWarning(mapEx, "Exception removing map {MapName}", mapName);
                }

                processed++;
                context.SetCustomStatus(new OrchestrationProgress("Remove", totalToProcess, processed, mapProgress));
            }

            if (failures.Count > 0)
            {
                var errorMessage = $"Failed to remove {failures.Count}/{safeToRemove.Count} maps: {string.Join("; ", failures)}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage));

                return;
            }

            // Update assignment to Removed
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Removed,
                    UnassignedAt: context.CurrentUtcDateTime,
                    LastError: "",
                    LastErrorAt: null));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RemoveMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    DeploymentState: DeploymentState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
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
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Activate, instanceId));

        try
        {
            // Update activation state to Activating
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Activating));

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId));

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds));

            if (mapNames.Count == 0)
            {
                throw new InvalidOperationException("No maps resolved for rotation activation");
            }

            // Initialize step progress tracking
            var isAacpVariable = (details.ConfigVariableName ?? "").StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase);
            var steps = new List<MapProgress>
            {
                new("Format rotation string", "Pending"),
                new("Write config variables", "Pending"),
                new("Set RCON dvars", "Pending"),
                new("Clear old overflow variables", "Pending")
            };
            if (!isAacpVariable)
            {
                steps.Add(new("Set g_gametype dvar", "Pending"));
            }
            var totalSteps = steps.Count;
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 0, steps));

            // Format the rotation string (may return multiple parts if >1024 chars)
            steps[0] = steps[0] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 0, steps));

            var rotationOutput = await context.CallActivityAsync<FormatRotationOutput>(
                nameof(MapRotationActivities.FormatRotationString),
                new FormatRotationInput(mapNames, details.GameMode ?? "war", details.ConfigVariableName ?? "sv_maprotation"));

            steps[0] = steps[0] with { Status = $"Completed ({rotationOutput.Parts.Count} part{(rotationOutput.Parts.Count != 1 ? "s" : "")})" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 1, steps));

            // Write config variables (base variable must exist; overflow vars are best-effort)
            steps[1] = steps[1] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 1, steps));

            var configSuccess = true;
            string? configError = null;
            for (var i = 0; i < rotationOutput.Parts.Count; i++)
            {
                var part = rotationOutput.Parts[i];
                var result = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.WriteConfigVariable),
                    new WriteConfigInput(details.GameServerId, details.ConfigFilePath ?? "", part.VariableName, part.Value));

                if (!result.Success)
                {
                    if (i == 0)
                    {
                        // Base variable failure is critical
                        configSuccess = false;
                        configError = result.Error;
                    }
                    else
                    {
                        // Overflow variable failure is non-critical (var may not exist in config file)
                        logger.LogWarning("Failed to write overflow config variable {VarName} for assignment {AssignmentId}: {Error}. " +
                            "The variable may not exist in the config file. RCON will still apply the full rotation at runtime.",
                            part.VariableName, input.AssignmentId, result.Error);
                    }
                }
            }

            steps[1] = configSuccess
                ? steps[1] with { Status = "Completed" }
                : steps[1] with { Status = "Failed", Error = configError };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 2, steps));

            // Set RCON dvars (all parts)
            steps[2] = steps[2] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 2, steps));

            var rconSuccess = true;
            string? rconError = null;
            foreach (var part in rotationOutput.Parts)
            {
                var result = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SetRconDvar),
                    new SetRconDvarInput(details.GameServerId, part.VariableName, part.Value));

                if (!result.Success)
                {
                    rconSuccess = false;
                    rconError ??= result.Error;
                }
            }

            steps[2] = rconSuccess
                ? steps[2] with { Status = "Completed" }
                : steps[2] with { Status = "Failed", Error = rconError };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 3, steps));

            // Clear old overflow variables that are no longer needed (best-effort)
            steps[3] = steps[3] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 3, steps));

            var usedVarNames = rotationOutput.Parts.Select(p => p.VariableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseVar = details.ConfigVariableName ?? "sv_maprotation";
            foreach (var overflowVar in RotationVariableNaming.GetOverflowVariableNames(baseVar, usedVarNames))
            {
                // Best-effort clear via RCON (config file clear may fail if var doesn't exist)
                await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SetRconDvar),
                    new SetRconDvarInput(details.GameServerId, overflowVar, ""));
            }

            steps[3] = steps[3] with { Status = "Completed" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 4, steps));

            // Set g_gametype via RCON (best-effort, non-blocking for non-AACP variables)
            if (!isAacpVariable && configSuccess && rconSuccess)
            {
                var gametypeStepIdx = steps.Count - 1;
                steps[gametypeStepIdx] = steps[gametypeStepIdx] with { Status = "InProgress" };
                context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 4, steps));

                var gameMode = details.GameMode ?? "war";
                var gametypeResult = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SetRconDvar),
                    new SetRconDvarInput(details.GameServerId, "g_gametype", gameMode));

                steps[gametypeStepIdx] = gametypeResult.Success
                    ? steps[gametypeStepIdx] with { Status = "Completed" }
                    : steps[gametypeStepIdx] with { Status = "Failed", Error = gametypeResult.Error };
                context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, totalSteps, steps));

                if (!gametypeResult.Success)
                {
                    logger.LogWarning("Failed to set g_gametype to {GameMode} for assignment {AssignmentId}: {Error}",
                        gameMode, input.AssignmentId, gametypeResult.Error);
                }
            }

            // Both must succeed for activation to be considered complete
            if (!configSuccess || !rconSuccess)
            {
                var errors = new List<string>();
                if (!configSuccess) errors.Add($"Config write failed: {configError}");
                if (!rconSuccess) errors.Add($"RCON set failed: {rconError}");
                var errorMessage = string.Join("; ", errors);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: errorMessage,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage));

                return;
            }

            // Update activation state to Active with version
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Active,
                    ActivatedVersion: details.RotationVersion,
                    LastError: "",
                    LastErrorAt: null));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ActivateMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
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
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Deactivate, instanceId));

        try
        {
            // Update activation state to Deactivating
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Deactivating));

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId));

            // Initialize step progress tracking
            var steps = new List<MapProgress>
            {
                new("Write config file", "Pending"),
                new("Set RCON dvar", "Pending"),
                new("Clear overflow variables", "Pending")
            };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 0, steps));

            // Write empty value to config variable
            steps[0] = steps[0] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 0, steps));

            var configResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.WriteConfigVariable),
                new WriteConfigInput(
                    details.GameServerId,
                    details.ConfigFilePath ?? "",
                    details.ConfigVariableName ?? "sv_maprotation",
                    ""));

            steps[0] = configResult.Success
                ? steps[0] with { Status = "Completed" }
                : steps[0] with { Status = "Failed", Error = configResult.Error };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 1, steps));

            // Set RCON dvar to empty
            steps[1] = steps[1] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 1, steps));

            var rconResult = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.SetRconDvar),
                new SetRconDvarInput(
                    details.GameServerId,
                    details.ConfigVariableName ?? "sv_maprotation",
                    ""));

            steps[1] = rconResult.Success
                ? steps[1] with { Status = "Completed" }
                : steps[1] with { Status = "Failed", Error = rconResult.Error };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 2, steps));

            // Clear overflow variables via RCON (best-effort)
            steps[2] = steps[2] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 2, steps));

            var baseVar = details.ConfigVariableName ?? "sv_maprotation";
            var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var overflowVar in RotationVariableNaming.GetOverflowVariableNames(baseVar, emptySet))
            {
                await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SetRconDvar),
                    new SetRconDvarInput(details.GameServerId, overflowVar, ""));
            }

            steps[2] = steps[2] with { Status = "Completed" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 3, steps));

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
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage));

                return;
            }

            // Update activation state to Inactive
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Inactive,
                    LastError: "",
                    LastErrorAt: null));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DeactivateMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId,
                    ActivationState: ActivationState.Failed,
                    LastError: ex.Message,
                    LastErrorAt: context.CurrentUtcDateTime));

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
        }
    }

    [Function(nameof(VerifyMapRotationOrchestrator))]
    public static async Task VerifyMapRotationOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<VerifyOrchestrationInput>()
            ?? throw new InvalidOperationException("VerifyOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(VerifyMapRotationOrchestrator));
        var instanceId = context.InstanceId;

        var operationId = await context.CallActivityAsync<Guid>(
            nameof(MapRotationActivities.RecordOperation),
            new RecordOperationInput(input.AssignmentId, AssignmentOperationType.Verify, instanceId));

        try
        {
            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId));

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds));

            // Initialize progress
            var mapProgress = mapNames.Select(m => new MapProgress(m, "Pending")).ToList();
            context.SetCustomStatus(new OrchestrationProgress("Verify", mapNames.Count, 0, mapProgress));

            // Get maps currently loaded on server via FTP
            var loadedMaps = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.GetLoadedMapsFromServer),
                new GetLoadedMapsInput(details.GameServerId));

            // Check each map against loaded maps
            var missing = new List<string>();
            for (var i = 0; i < mapNames.Count; i++)
            {
                var mapName = mapNames[i];
                var isPresent = loadedMaps.Any(m => string.Equals(m, mapName, StringComparison.OrdinalIgnoreCase));

                mapProgress[i] = mapProgress[i] with
                {
                    Status = isPresent ? "Completed" : "Failed",
                    Error = isPresent ? null : "Not found on server"
                };

                if (!isPresent)
                    missing.Add(mapName);

                context.SetCustomStatus(new OrchestrationProgress("Verify", mapNames.Count, i + 1, mapProgress));
            }

            if (missing.Count > 0)
            {
                var errorMessage = $"Verification found {missing.Count}/{mapNames.Count} maps missing from server: {string.Join(", ", missing)}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, errorMessage));

                return;
            }

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "VerifyMapRotation orchestration failed for assignment {AssignmentId}", input.AssignmentId);

            await context.CallActivityAsync(
                nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
        }
    }
}
