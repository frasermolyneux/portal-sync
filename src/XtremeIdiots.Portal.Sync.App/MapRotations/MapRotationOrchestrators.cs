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

            // AACP assignments share maps with the main rotation — skip file uploads
            var isAacpVariable = (details.ConfigVariableName ?? "").StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase);
            if (isAacpVariable)
            {
                logger.LogInformation(
                    "AACP assignment detected for {AssignmentId} — skipping file uploads for {MapCount} maps (maps assumed present via main rotation)",
                    input.AssignmentId, mapNames.Count);

                for (var i = 0; i < mapProgress.Count; i++)
                {
                    mapProgress[i] = mapProgress[i] with { Status = "Skipped", Error = SkipReasons.AacpSharedMaps };
                }
                context.SetCustomStatus(new OrchestrationProgress("Sync", mapNames.Count, mapNames.Count, mapProgress));

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

                return;
            }

            // Push each map sequentially to avoid host overload
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
                        new SyncMapInput(details.GameServerId, mapName, details.GameType));

                    if (result.SkipReason != null)
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Skipped", Error = result.SkipReason };
                    }
                    else if (result.Success)
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

            var skippedNoFiles = mapProgress.Where(p => p.Status == "Skipped" && p.Error == SkipReasons.NoMapFiles).ToList();

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
            else if (skippedNoFiles.Count > 0)
            {
                var warningMessage = $"Partially deployed: {skippedNoFiles.Count} map(s) skipped (no files available): {string.Join(", ", skippedNoFiles.Select(s => s.MapName))}";

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.PartiallyDeployed,
                        DeployedVersion: details.RotationVersion,
                        LastError: warningMessage,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Succeeded));
            }
            else
            {
                // All succeeded or were built-in skips
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
                {
                    continue;
                }

                var mapName = mapNames[i];
                mapProgress[i] = mapProgress[i] with { Status = "InProgress" };
                context.SetCustomStatus(new OrchestrationProgress("Remove", totalToProcess, processed, mapProgress));

                try
                {
                    var result = await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.RemoveSingleMapFromServer),
                        new RemoveMapInput(details.GameServerId, mapName, details.GameType));

                    if (result.SkipReason != null)
                    {
                        mapProgress[i] = mapProgress[i] with { Status = "Skipped", Error = result.SkipReason };
                    }
                    else if (result.Success)
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

            try
            {
                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        DeploymentState: DeploymentState.Failed,
                        LastError: ex.Message,
                        LastErrorAt: context.CurrentUtcDateTime));
            }
            catch (Exception statusUpdateEx)
            {
                logger.LogWarning(
                    statusUpdateEx,
                    "Failed to persist failed deployment state for assignment {AssignmentId} during remove failure handling",
                    input.AssignmentId);
            }

            try
            {
                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, ex.Message));
            }
            catch (Exception completeOperationEx)
            {
                logger.LogError(
                    completeOperationEx,
                    "Failed to complete remove operation {OperationId} for assignment {AssignmentId} during failure handling",
                    operationId,
                    input.AssignmentId);
                throw;
            }
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

        var activationDeadline = context.CurrentUtcDateTime.Add(MapRotationOrchestrationPolicies.ActivationTimeout);

        static bool HasTimedOut(TaskOrchestrationContext orchestrationContext, DateTime activationDeadlineUtc)
        {
            return orchestrationContext.CurrentUtcDateTime >= activationDeadlineUtc;
        }

        static string BuildTimeoutError(DateTime activationDeadlineUtc)
        {
            return $"Activation timed out after {(int)MapRotationOrchestrationPolicies.ActivationTimeout.TotalMinutes} minutes (deadline: {activationDeadlineUtc:O}).";
        }

        try
        {
            // Update activation state to Activating
            await context.CallActivityAsync(
                nameof(MapRotationActivities.UpdateAssignmentStatus),
                new UpdateStatusInput(input.AssignmentId, ActivationState: ActivationState.Activating),
                MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

            if (HasTimedOut(context, activationDeadline))
            {
                var timeoutError = BuildTimeoutError(activationDeadline);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: timeoutError,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, timeoutError));

                return;
            }

            // Get rotation details
            var details = await context.CallActivityAsync<RotationDetails>(
                nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(input.AssignmentId),
                MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

            if (HasTimedOut(context, activationDeadline))
            {
                var timeoutError = BuildTimeoutError(activationDeadline);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: timeoutError,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, timeoutError));

                return;
            }

            var supportsRconDvar = details.GameType == GameType.CallOfDuty4x;

            // Resolve map IDs to names
            var mapNames = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.ResolveMapNames),
                new ResolveMapNamesInput(details.MapIds),
                MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

            if (HasTimedOut(context, activationDeadline))
            {
                var timeoutError = BuildTimeoutError(activationDeadline);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: timeoutError,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, timeoutError));

                return;
            }

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
                new FormatRotationInput(mapNames, details.GameMode ?? "war", details.ConfigVariableName ?? "sv_maprotation"),
                MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

            if (HasTimedOut(context, activationDeadline))
            {
                var timeoutError = BuildTimeoutError(activationDeadline);

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: timeoutError,
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, timeoutError));

                return;
            }

            steps[0] = steps[0] with { Status = "Completed" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 1, steps));

            // Write config variables (base variable must exist; overflow vars are best-effort)
            steps[1] = steps[1] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 1, steps));

            var configSuccess = true;
            string? configError = null;
            var portalUrl = $"https://portal.xtremeidiots.com/MapRotations/Details/{details.MapRotationId}";
            var managedCommentLines = new[]
            {
                $"Managed by XtremeIdiots Portal - {details.Title ?? "Map Rotation"}",
                portalUrl,
                "Do not edit manually - changes will be overwritten on next activation"
            };

            for (var i = 0; i < rotationOutput.Parts.Count; i++)
            {
                if (HasTimedOut(context, activationDeadline))
                {
                    configSuccess = false;
                    configError = BuildTimeoutError(activationDeadline);
                    break;
                }

                var part = rotationOutput.Parts[i];
                // Only attach the managed comment block to the first (base) variable
                var commentLines = i == 0 ? managedCommentLines : null;
                var result = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.WriteConfigVariable),
                    new WriteConfigInput(details.GameServerId, details.ConfigFilePath ?? "", part.VariableName, part.Value, commentLines),
                    MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

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

            // Early exit if config write failed — don't attempt RCON on a broken config
            if (!configSuccess)
            {
                await context.CallActivityAsync(
                    nameof(MapRotationActivities.UpdateAssignmentStatus),
                    new UpdateStatusInput(input.AssignmentId,
                        ActivationState: ActivationState.Failed,
                        LastError: $"Config write failed: {configError}",
                        LastErrorAt: context.CurrentUtcDateTime));

                await context.CallActivityAsync(
                    nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId, AssignmentOperationStatus.Failed, $"Config write failed: {configError}"));

                return;
            }

            // Set RCON dvars (all parts)
            steps[2] = steps[2] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 2, steps));

            var rconSuccess = true;
            string? rconError = null;

            if (supportsRconDvar)
            {
                foreach (var part in rotationOutput.Parts)
                {
                    if (HasTimedOut(context, activationDeadline))
                    {
                        rconSuccess = false;
                        rconError ??= BuildTimeoutError(activationDeadline);
                        break;
                    }

                    var result = await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.SetRconDvar),
                        new SetRconDvarInput(details.GameServerId, details.GameType, part.VariableName, part.Value),
                        MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

                    if (!result.Success)
                    {
                        rconSuccess = false;
                        rconError ??= result.Error;
                    }
                }
            }
            else
            {
                rconError = $"RCON dvar updates are not supported for game type '{details.GameType}'.";
            }

            steps[2] = supportsRconDvar
                ? (rconSuccess
                    ? steps[2] with { Status = "Completed" }
                    : steps[2] with { Status = "Failed", Error = rconError })
                : steps[2] with { Status = "Skipped", Error = rconError };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 3, steps));

            // Clear old overflow variables that are no longer needed (best-effort)
            steps[3] = steps[3] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 3, steps));

            var usedVarNames = rotationOutput.Parts.Select(p => p.VariableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baseVar = details.ConfigVariableName ?? "sv_maprotation";
            if (supportsRconDvar)
            {
                foreach (var overflowVar in RotationVariableNaming.GetOverflowVariableNames(baseVar, usedVarNames))
                {
                    if (HasTimedOut(context, activationDeadline))
                    {
                        break;
                    }

                    // Best-effort clear via RCON (config file clear may fail if var doesn't exist)
                    await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.SetRconDvar),
                        new SetRconDvarInput(details.GameServerId, details.GameType, overflowVar, ""),
                        MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);
                }
            }

            steps[3] = supportsRconDvar
                ? steps[3] with { Status = "Completed" }
                : steps[3] with { Status = "Skipped", Error = rconError };
            context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 4, steps));

            // Set g_gametype via RCON (best-effort, non-blocking for non-AACP variables)
            if (!isAacpVariable && configSuccess && rconSuccess)
            {
                var gametypeStepIdx = steps.Count - 1;
                if (!supportsRconDvar)
                {
                    steps[gametypeStepIdx] = steps[gametypeStepIdx] with { Status = "Skipped", Error = rconError };
                    context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, totalSteps, steps));
                }
                else
                {
                    steps[gametypeStepIdx] = steps[gametypeStepIdx] with { Status = "InProgress" };
                    context.SetCustomStatus(new OrchestrationProgress("Activate", totalSteps, 4, steps));

                    var gameMode = details.GameMode ?? "war";
                    var gametypeResult = await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.SetRconDvar),
                        new SetRconDvarInput(details.GameServerId, details.GameType, "g_gametype", gameMode),
                        MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);

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
            }

            // Both must succeed for activation to be considered complete
            if (!configSuccess || !rconSuccess)
            {
                var errors = new List<string>();
                if (!configSuccess)
                {
                    errors.Add($"Config write failed: {configError}");
                }

                if (!rconSuccess)
                {
                    errors.Add($"RCON set failed: {rconError}");
                }

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

            var supportsRconDvar = details.GameType == GameType.CallOfDuty4x;
            var unsupportedRconError = $"RCON dvar updates are not supported for game type '{details.GameType}'.";

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
                    "",
                    Array.Empty<string>()));

            steps[0] = configResult.Success
                ? steps[0] with { Status = "Completed" }
                : steps[0] with { Status = "Failed", Error = configResult.Error };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 1, steps));

            // Set RCON dvar to empty
            steps[1] = steps[1] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 1, steps));

            MapOperationResult rconResult;
            if (supportsRconDvar)
            {
                rconResult = await context.CallActivityAsync<MapOperationResult>(
                    nameof(MapRotationActivities.SetRconDvar),
                    new SetRconDvarInput(
                        details.GameServerId,
                        details.GameType,
                        details.ConfigVariableName ?? "sv_maprotation",
                        ""));

                steps[1] = rconResult.Success
                    ? steps[1] with { Status = "Completed" }
                    : steps[1] with { Status = "Failed", Error = rconResult.Error };
            }
            else
            {
                rconResult = new MapOperationResult(details.ConfigVariableName ?? "sv_maprotation", true, SkipReason: unsupportedRconError);
                steps[1] = steps[1] with { Status = "Skipped", Error = unsupportedRconError };
            }
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 2, steps));

            // Clear overflow variables via RCON (best-effort)
            steps[2] = steps[2] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 2, steps));

            var baseVar = details.ConfigVariableName ?? "sv_maprotation";
            var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (supportsRconDvar)
            {
                foreach (var overflowVar in RotationVariableNaming.GetOverflowVariableNames(baseVar, emptySet))
                {
                    await context.CallActivityAsync<MapOperationResult>(
                        nameof(MapRotationActivities.SetRconDvar),
                        new SetRconDvarInput(details.GameServerId, details.GameType, overflowVar, ""));
                }
            }

            steps[2] = supportsRconDvar
                ? steps[2] with { Status = "Completed" }
                : steps[2] with { Status = "Skipped", Error = unsupportedRconError };
            context.SetCustomStatus(new OrchestrationProgress("Deactivate", 3, 3, steps));

            // Both must succeed for deactivation to be considered complete
            if (!configResult.Success || !rconResult.Success)
            {
                var errors = new List<string>();
                if (!configResult.Success)
                {
                    errors.Add($"Config write failed: {configResult.Error}");
                }

                if (!rconResult.Success)
                {
                    errors.Add($"RCON set failed: {rconResult.Error}");
                }

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

            // Get maps currently loaded on server host
            var loadedMaps = await context.CallActivityAsync<List<string>>(
                nameof(MapRotationActivities.GetLoadedMapsFromServer),
                new GetLoadedMapsInput(details.GameServerId));

            // Check each map against loaded maps (skip built-in maps)
            var missing = new List<string>();
            for (var i = 0; i < mapNames.Count; i++)
            {
                var mapName = mapNames[i];

                if (BuiltInMaps.IsBuiltIn(details.GameType, mapName))
                {
                    mapProgress[i] = mapProgress[i] with
                    {
                        Status = "Skipped",
                        Error = SkipReasons.BuiltInMap
                    };

                    context.SetCustomStatus(new OrchestrationProgress("Verify", mapNames.Count, i + 1, mapProgress));
                    continue;
                }

                var isPresent = loadedMaps.Any(m => string.Equals(m, mapName, StringComparison.OrdinalIgnoreCase));

                mapProgress[i] = mapProgress[i] with
                {
                    Status = isPresent ? "Completed" : "Failed",
                    Error = isPresent ? null : "Not found on server"
                };

                if (!isPresent)
                {
                    missing.Add(mapName);
                }

                context.SetCustomStatus(new OrchestrationProgress("Verify", mapNames.Count, i + 1, mapProgress));
            }

            if (missing.Count > 0)
            {
                var verifiedCount = mapNames.Count - mapProgress.Count(p => p.Status == "Skipped");
                var errorMessage = $"Verification found {missing.Count}/{verifiedCount} maps missing from server: {string.Join(", ", missing)}";

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

    [Function(nameof(PushMapToServerOrchestrator))]
    public static async Task PushMapToServerOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<PushMapOrchestrationInput>()
            ?? throw new InvalidOperationException("PushMapOrchestrationInput is required");

        var logger = context.CreateReplaySafeLogger(nameof(PushMapToServerOrchestrator));

        var steps = new List<MapProgress>
        {
            new(input.MapName, "Pending")
        };
        context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 0, steps));

        try
        {
            steps[0] = steps[0] with { Status = "InProgress" };
            context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 0, steps));

            var result = await context.CallActivityAsync<MapOperationResult>(
                nameof(MapRotationActivities.SyncSingleMapToServer),
                new SyncMapInput(input.GameServerId, input.MapName, input.GameType));

            if (result.SkipReason != null)
            {
                steps[0] = steps[0] with { Status = "Skipped", Error = result.SkipReason };
                context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 1, steps));
                logger.LogInformation("Skipped map {MapName} for server {GameServerId}: {Reason}", input.MapName, input.GameServerId, result.SkipReason);
            }
            else if (result.Success)
            {
                steps[0] = steps[0] with { Status = "Completed" };
                context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 1, steps));
                logger.LogInformation("Successfully pushed map {MapName} to server {GameServerId}", input.MapName, input.GameServerId);
            }
            else
            {
                steps[0] = steps[0] with { Status = "Failed", Error = result.Error };
                context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 1, steps));
                logger.LogWarning("Failed to push map {MapName} to server {GameServerId}: {Error}", input.MapName, input.GameServerId, result.Error);
            }
        }
        catch (Exception ex)
        {
            steps[0] = steps[0] with { Status = "Failed", Error = ex.Message };
            context.SetCustomStatus(new OrchestrationProgress("PushMap", 1, 1, steps));
            logger.LogError(ex, "PushMapToServer orchestration failed for {MapName} on {GameServerId}", input.MapName, input.GameServerId);
        }
    }
}
