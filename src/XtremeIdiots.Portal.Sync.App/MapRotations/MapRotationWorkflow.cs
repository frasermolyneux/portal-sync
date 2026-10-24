using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

// New entry points leave persisted V1 orchestration histories replayable.
public static class MapRotationWorkflow
{
    public const string CancellationEvent = "Cancel";

    [Function(nameof(RepairMapRotationV2))]
    public static Task<RotationWorkflowResult> RepairMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<SyncOrchestrationInput>()!.AssignmentId, "Repair");

    [Function(nameof(ApplyMapRotationV2))]
    public static Task<RotationWorkflowResult> ApplyMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<SyncOrchestrationInput>()!.AssignmentId, "Apply");

    [Function(nameof(ActivateMapRotationV2))]
    public static Task<RotationWorkflowResult> ActivateMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<ActivateOrchestrationInput>()!.AssignmentId, "Activate");

    [Function(nameof(DeactivateMapRotationV2))]
    public static Task<RotationWorkflowResult> DeactivateMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<DeactivateOrchestrationInput>()!.AssignmentId, "Deactivate");

    [Function(nameof(UnassignMapRotationV2))]
    public static Task<RotationWorkflowResult> UnassignMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<RemoveOrchestrationInput>()!.AssignmentId, "Unassign");

    [Function(nameof(VerifyMapRotationV2))]
    public static Task<RotationWorkflowResult> VerifyMapRotationV2([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<VerifyOrchestrationInput>()!.AssignmentId, "Verify");

    [Function(nameof(PushMapToServerV2))]
    public static async Task<RotationWorkflowResult> PushMapToServerV2([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<PushMapOrchestrationInput>()
            ?? throw new InvalidOperationException("A map push input is required.");
        var cancellation = context.WaitForExternalEvent<object>(CancellationEvent);
        Progress(context, "PushMap", input.MapName, "InProgress");
        await using var serverLock = await context.Entities.LockEntitiesAsync(
            new EntityInstanceId(nameof(MapRotationServerLock), input.GameServerId.ToString()));
        try
        {
            if (cancellation.IsCompleted)
            {
                throw new OperationCanceledException("Map push cancelled before starting the transfer.");
            }
            var result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.SyncSingleMapToServer),
                new SyncMapInput(input.GameServerId, input.MapName, input.GameType));
            RequireSuccess(result);
            Progress(context, "PushMap", input.MapName, "Completed");
            return new RotationWorkflowResult(true);
        }
        catch (Exception ex)
        {
            context.CreateReplaySafeLogger(nameof(MapRotationWorkflow)).LogError(ex, "Map push failed on server {ServerId}", input.GameServerId);
            Progress(context, "PushMap", input.MapName, "Failed", ex.Message);
            return new RotationWorkflowResult(false, ex.Message);
        }
    }

    private static async Task<RotationWorkflowResult> Run(TaskOrchestrationContext context, Guid assignmentId, string action)
    {
        var cancellation = context.WaitForExternalEvent<object>(CancellationEvent);
        var details = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
            new GetRotationDetailsInput(assignmentId));
        Progress(context, action, "Waiting for this server's current operation", "InProgress");
        await using var serverLock = await context.Entities.LockEntitiesAsync(
            new EntityInstanceId(nameof(MapRotationServerLock), details.GameServerId.ToString()));
        var snapshot = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
            new GetRotationDetailsInput(assignmentId));
        if (snapshot.GameServerId != details.GameServerId)
        {
            throw new InvalidOperationException("The assignment's server changed before its server lock was acquired.");
        }
        var linked = action == "Apply"
            ? await context.CallActivityAsync<List<LinkedConfigurationTarget>>(nameof(MapRotationActivities.GetLinkedConfigurationAssignments),
                new LinkedConfigurationInput(snapshot.GameServerId, snapshot.MapRotationId, assignmentId, snapshot.ConfigVariableName))
            : [];
        var result = await RunLocked(context, assignmentId, snapshot.GameServerId, action, cancellation, snapshot);
        if (result.Success && action == "Apply")
        {
            const string driftError = "A selected/linked slot changed during Apply. Review the group and apply it again.";
            foreach (var target in linked)
            {
                var selected = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
                    new GetRotationDetailsInput(assignmentId));
                if (!IsAppliedSnapshot(selected, snapshot))
                {
                    Progress(context, action, "Validate selected configuration slot", "Failed", driftError);
                    return new RotationWorkflowResult(false, driftError);
                }
                var expected = snapshot with
                {
                    AssignmentId = target.AssignmentId,
                    ConfigFilePath = target.ConfigFilePath,
                    ConfigVariableName = target.ConfigVariableName
                };
                result = await RunLocked(context, target.AssignmentId, snapshot.GameServerId, "Activate", cancellation, expected);
                if (!result.Success)
                {
                    Progress(context, action, "Linked configuration slot", "Failed", result.Error);
                    return result;
                }
            }
            foreach (var target in linked.Prepend(new LinkedConfigurationTarget(assignmentId, snapshot.ConfigFilePath, snapshot.ConfigVariableName)))
            {
                var current = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
                    new GetRotationDetailsInput(target.AssignmentId));
                if (!IsAppliedSnapshot(current, snapshot with
                {
                    ConfigFilePath = target.ConfigFilePath,
                    ConfigVariableName = target.ConfigVariableName
                }))
                {
                    Progress(context, action, "Validate linked configuration group", "Failed", driftError);
                    return new RotationWorkflowResult(false, driftError);
                }
            }
            Progress(context, action, "Map files verified and selected/linked configuration slots applied", "Completed");
        }
        return result;
    }

    internal static async Task<RotationWorkflowResult> RunLocked(
        TaskOrchestrationContext context, Guid assignmentId, Guid serverId, string action, Task cancellation,
        RotationDetails? expected = null)
    {
        Guid? operationId = null;
        var targetValidated = false;
        var configuring = action is "Activate" or "Deactivate";
        var deadline = context.CurrentUtcDateTime.AddMinutes(30);
        var logger = context.CreateReplaySafeLogger(nameof(MapRotationWorkflow));

        void Check()
        {
            if (cancellation.IsCompleted)
            {
                throw new OperationCanceledException("Cancellation requested. The current service call has finished; no further server changes will be started.");
            }
            if (context.CurrentUtcDateTime >= deadline)
            {
                throw new TimeoutException("The operation exceeded its 30-minute processing window. Verify files/configuration before retrying.");
            }
        }

        async Task SetState(UpdateStatusInput update)
        {
            await context.CallActivityAsync(nameof(MapRotationActivities.UpdateAssignmentStatus), update);
        }

        async Task StartOperation(AssignmentOperationType type)
        {
            operationId = await context.CallActivityAsync<Guid>(nameof(MapRotationActivities.RecordOperation),
                new RecordOperationInput(assignmentId, type, context.InstanceId));
        }

        async Task Complete()
        {
            await context.CallActivityAsync(nameof(MapRotationActivities.CompleteOperation),
                new CompleteOperationInput(operationId!.Value, AssignmentOperationStatus.Succeeded));
            operationId = null;
        }

        async Task ConfirmSnapshot(RotationDetails snapshot)
        {
            Check();
            var current = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(assignmentId));
            if (!MatchesSnapshot(current, snapshot))
            {
                throw new InvalidOperationException("The rotation or configuration target changed during this operation. Review the current assignment and apply it again.");
            }
        }

        async Task<List<MapOperationResult>> Verify(RotationDetails rotation, List<string> names)
        {
            Check();
            return await context.CallActivityAsync<List<MapOperationResult>>(nameof(MapRotationActivities.VerifyMapFiles),
                new VerifyMapFilesInput(rotation.GameServerId, names),
                MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);
        }

        async Task SetRuntime(SetRconDvarInput input)
        {
            Check();
            var result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.SetRconDvar), input);
            Check();
            if (!result.Success)
            {
                await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(10), CancellationToken.None);
                Check();
                result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.SetRconDvar), input);
                Check();
            }
            RequireSuccess(result);
        }

        async Task WriteConfig(string activity, WriteConfigInput input)
        {
            Check();
            try
            {
                await context.CallActivityAsync(activity, input);
            }
            catch (TaskFailedException)
            {
                Check();
                await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(10), CancellationToken.None);
                Check();
                await context.CallActivityAsync(activity, input);
            }
            Check();
        }

        try
        {
            await StartOperation(action switch
            {
                "Repair" or "Apply" => AssignmentOperationType.Sync,
                "Verify" => AssignmentOperationType.Verify,
                "Unassign" => AssignmentOperationType.Remove,
                "Deactivate" => AssignmentOperationType.Deactivate,
                _ => AssignmentOperationType.Activate
            });
            Check();
            var details = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(assignmentId));
            if (details.GameServerId != serverId || details.DeploymentState == DeploymentState.Removed)
            {
                throw new InvalidOperationException("The assignment was removed or its server changed while this operation was waiting.");
            }
            if (expected != null && !MatchesSnapshot(details, expected))
            {
                throw new InvalidOperationException("The expected rotation version or linked configuration target changed. No further server changes will be started for this slot.");
            }
            targetValidated = true;

            if (action == "Unassign")
            {
                if (details.ActivationState != ActivationState.Inactive)
                {
                    throw new InvalidOperationException("Deactivate this configuration slot successfully before unassigning it. Map files will be retained.");
                }
                Check();
                await SetState(new UpdateStatusInput(assignmentId, DeploymentState: DeploymentState.Removed,
                    UnassignedAt: context.CurrentUtcDateTime, LastError: ""));
                await Complete();
                Progress(context, action, "Unassigned; server map files retained", "Completed");
                return new RotationWorkflowResult(true);
            }

            var names = action == "Deactivate" ? [] :
                await context.CallActivityAsync<List<string>>(nameof(MapRotationActivities.ResolveMapNames),
                    new ResolveMapNamesInput(details.MapIds));
            if (names.Count == 0 && action != "Deactivate")
            {
                throw new InvalidOperationException("This rotation has no resolvable maps.");
            }

            if (action is "Repair" or "Apply")
            {
                await SetState(new UpdateStatusInput(assignmentId, DeploymentState: DeploymentState.Syncing));
                var mapProgress = names.Select(name => new MapProgress(name, "Pending")).ToList();
                for (var index = 0; index < names.Count; index++)
                {
                    Check();
                    mapProgress[index] = mapProgress[index] with { Status = "InProgress" };
                    context.SetCustomStatus(new OrchestrationProgress(action, names.Count, index, mapProgress));
                    var result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.RepairSingleMapToServer),
                        new SyncMapInput(serverId, names[index], details.GameType));
                    if (!result.Success)
                    {
                        Check();
                        await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(10), CancellationToken.None);
                        Check();
                        result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.RepairSingleMapToServer),
                            new SyncMapInput(serverId, names[index], details.GameType));
                    }
                    mapProgress[index] = mapProgress[index] with
                    {
                        Status = result.Success ? (result.SkipReason is null ? "Completed" : "Skipped") : "Failed",
                        Error = result.Error ?? result.SkipReason
                    };
                    context.SetCustomStatus(new OrchestrationProgress(action, names.Count, index + 1, mapProgress));
                    if (!result.Success)
                    {
                        throw new InvalidOperationException($"{names[index]}: {result.Error}");
                    }
                }
                RequireComplete(await Verify(details, names));
                Check();
                await ConfirmSnapshot(details);
                await SetState(new UpdateStatusInput(assignmentId, DeploymentState: DeploymentState.Synced,
                    DeployedVersion: details.RotationVersion,
                    LastError: details.ActivationState == ActivationState.Failed ? null : ""));
                await Complete();
                if (action == "Repair")
                {
                    Progress(context, action, "Map files verified; configuration unchanged", "Completed");
                    return new RotationWorkflowResult(true);
                }
                configuring = true;
                await StartOperation(AssignmentOperationType.Activate);
            }

            if (action == "Verify")
            {
                RequireComplete(await Verify(details, names));
                Check();
                await ConfirmSnapshot(details);
                await SetState(new UpdateStatusInput(assignmentId, DeploymentState: DeploymentState.Synced,
                    DeployedVersion: details.RotationVersion,
                    LastError: details.ActivationState == ActivationState.Failed ? null : ""));
                await Complete();
                Progress(context, action, "Required map files and sizes verified", "Completed");
                return new RotationWorkflowResult(true);
            }

            Check();
            if (string.IsNullOrWhiteSpace(details.ConfigFilePath) || string.IsNullOrWhiteSpace(details.ConfigVariableName))
            {
                throw new InvalidOperationException("Choose a configuration file and variable for this server slot.");
            }
            if (!MapRotationRconCapabilities.SupportsSetDvar(details.GameType))
            {
                throw new InvalidOperationException("This game does not support the RCON variable updates needed to confirm activation.");
            }
            if (action != "Deactivate")
            {
                RequireComplete(await Verify(details, names));
                await SetState(new UpdateStatusInput(assignmentId, DeploymentState: DeploymentState.Synced,
                    DeployedVersion: details.RotationVersion));
            }
            await ConfirmSnapshot(details);
            await SetState(new UpdateStatusInput(assignmentId,
                ActivationState: action == "Deactivate" ? ActivationState.Deactivating : ActivationState.Activating));

            var formatted = action == "Deactivate"
                ? new FormatRotationOutput([new RotationStringPart(details.ConfigVariableName, "")])
                : await context.CallActivityAsync<FormatRotationOutput>(nameof(MapRotationActivities.FormatRotationString),
                    new FormatRotationInput(names, details.GameMode ?? "war", details.ConfigVariableName));
            var isAacp = details.ConfigVariableName.StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase);
            var aacpVariables = Enumerable.Range(1, 10).Select(index => $"scr_aacp_maps_{index}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (formatted.Parts.Count == 0 || formatted.Parts.Any(part => part.Value.Length > 1024) ||
                (!isAacp && formatted.Parts.Count > 1) || (isAacp && (formatted.Parts.Count > 10 ||
                    !aacpVariables.Contains(details.ConfigVariableName) ||
                    formatted.Parts.Any(part => !aacpVariables.Contains(part.VariableName)))))
            {
                throw new InvalidOperationException("The rotation exceeds the server slot's supported variable limits (1024 characters; one standard variable or ten AACP parts). Shorten the rotation before applying.");
            }
            if (!isAacp)
            {
                formatted = new FormatRotationOutput([formatted.Parts[0] with { VariableName = details.ConfigVariableName }]);
            }
            var reserved = isAacp
                ? (await context.CallActivityAsync<List<string>>(nameof(MapRotationActivities.GetReservedConfigVariables),
                    new ConfigSlotInput(serverId, assignmentId, details.ConfigFilePath, details.ConfigVariableName)))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (formatted.Parts.Any(part => reserved.Contains(part.VariableName)))
            {
                throw new InvalidOperationException("An AACP continuation variable belongs to another configuration slot. Resolve the slot overlap before applying.");
            }
            var overflowVariables = isAacp
                ? RotationVariableNaming.GetOverflowVariableNames(details.ConfigVariableName,
                    formatted.Parts.Select(part => part.VariableName).ToHashSet(StringComparer.OrdinalIgnoreCase))
                    .Where(variable => aacpVariables.Contains(variable) && !reserved.Contains(variable)).ToList()
                : [];

            foreach (var part in formatted.Parts)
            {
                Check();
                Progress(context, action, $"Write and read back {part.VariableName}", "InProgress");
                await WriteConfig(nameof(MapRotationActivities.WriteAndVerifyConfigVariable),
                    new WriteConfigInput(serverId, details.ConfigFilePath, part.VariableName, part.Value,
                        action == "Deactivate" ? [] :
                        [$"Managed by XtremeIdiots Portal - {details.Title ?? "Map Rotation"}",
                         $"https://portal.xtremeidiots.com/MapRotations/Details/{details.MapRotationId}"]));
            }

            foreach (var variable in overflowVariables)
            {
                Check();
                await WriteConfig(nameof(MapRotationActivities.ClearConfigVariableIfPresent),
                    new WriteConfigInput(serverId, details.ConfigFilePath, variable, "", []));
            }

            foreach (var part in formatted.Parts.Concat(overflowVariables
                .Select(variable => new RotationStringPart(variable, ""))))
            {
                Check();
                Progress(context, action, $"Apply runtime variable {part.VariableName}", "InProgress");
                await SetRuntime(new SetRconDvarInput(serverId, details.GameType, part.VariableName, part.Value));
            }
            if (action != "Deactivate" && !details.ConfigVariableName.StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase))
            {
                Check();
                await SetRuntime(new SetRconDvarInput(serverId, details.GameType, "g_gametype", details.GameMode ?? "war"));
            }

            Check();
            await ConfirmSnapshot(details);
            await SetState(new UpdateStatusInput(assignmentId,
                ActivationState: action == "Deactivate" ? ActivationState.Inactive : ActivationState.Active,
                ActivatedVersion: action == "Deactivate" ? null : details.RotationVersion, LastError: ""));
            await Complete();
            Progress(context, action, "Configuration read back; runtime commands acknowledged", "Completed");
            return new RotationWorkflowResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Action} failed for assignment {AssignmentId}", action, assignmentId);
            Progress(context, action, action, "Failed", ex.Message);
            if (targetValidated)
            {
                await SetState(new UpdateStatusInput(assignmentId,
                    DeploymentState: action == "Unassign" || (configuring && ex is not InvalidDataException) ? null : DeploymentState.Failed,
                    ActivationState: configuring ? ActivationState.Failed : null,
                    LastError: ex.Message, LastErrorAt: context.CurrentUtcDateTime));
            }
            if (operationId.HasValue)
            {
                await context.CallActivityAsync(nameof(MapRotationActivities.CompleteOperation),
                    new CompleteOperationInput(operationId.Value,
                        ex is OperationCanceledException ? AssignmentOperationStatus.Cancelled : AssignmentOperationStatus.Failed, ex.Message));
            }
            return new RotationWorkflowResult(false, ex.Message);
        }
    }

    internal static void RequireComplete(List<MapOperationResult> results)
    {
        if (results.Count == 0)
        {
            throw new InvalidDataException("File verification returned no map results.");
        }
        var failures = results.Where(result => !result.Success).ToList();
        if (failures.Count != 0)
        {
            throw new InvalidDataException($"Map files incomplete: {string.Join("; ", failures.Select(result => $"{result.MapName}: {result.Error}"))}");
        }
    }

    private static bool MatchesSnapshot(RotationDetails current, RotationDetails snapshot) =>
        current.RotationVersion == snapshot.RotationVersion && current.MapRotationId == snapshot.MapRotationId &&
        current.GameServerId == snapshot.GameServerId && current.ConfigFilePath == snapshot.ConfigFilePath &&
        current.ConfigVariableName == snapshot.ConfigVariableName;

    private static bool IsAppliedSnapshot(RotationDetails current, RotationDetails snapshot) =>
        MatchesSnapshot(current, snapshot) && current.ActivationState == ActivationState.Active &&
        current.DeploymentState == DeploymentState.Synced && current.DeployedVersion == snapshot.RotationVersion;

    private static void RequireSuccess(MapOperationResult result)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"{result.MapName}: {result.Error}");
        }
    }

    private static void Progress(TaskOrchestrationContext context, string action, string step, string state, string? error = null) =>
        context.SetCustomStatus(new OrchestrationProgress(action, 1, state is "Completed" or "Failed" ? 1 : 0,
            [new MapProgress(step, state, error)]));
}

public sealed class MapRotationServerLock : TaskEntity<int>
{
    [Function(nameof(MapRotationServerLock))]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<MapRotationServerLock>();
}
