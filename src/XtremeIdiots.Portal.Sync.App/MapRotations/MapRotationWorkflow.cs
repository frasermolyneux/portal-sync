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
        var result = await RunLocked(context, assignmentId, details.GameServerId, action, cancellation);
        if (result.Success && action == "Apply")
        {
            details = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
                new GetRotationDetailsInput(assignmentId));
            var linked = await context.CallActivityAsync<List<Guid>>(nameof(MapRotationActivities.GetLinkedConfigurationAssignments),
                new LinkedConfigurationInput(details.GameServerId, details.MapRotationId, assignmentId, details.ConfigVariableName));
            foreach (var linkedId in linked)
            {
                result = await RunLocked(context, linkedId, details.GameServerId, "Activate", cancellation);
                if (!result.Success)
                {
                    Progress(context, action, "Linked configuration slot", "Failed", result.Error);
                    return result;
                }
            }
            Progress(context, action, "Map files verified and selected/linked configuration slots applied", "Completed");
        }
        return result;
    }

    internal static async Task<RotationWorkflowResult> RunLocked(
        TaskOrchestrationContext context, Guid assignmentId, Guid serverId, string action, Task cancellation)
    {
        Guid? operationId = null;
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
            if (current.RotationVersion != snapshot.RotationVersion || current.MapRotationId != snapshot.MapRotationId ||
                current.GameServerId != snapshot.GameServerId || current.ConfigFilePath != snapshot.ConfigFilePath ||
                current.ConfigVariableName != snapshot.ConfigVariableName)
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
                    var result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.SyncSingleMapToServer),
                        new SyncMapInput(serverId, names[index], details.GameType));
                    if (!result.Success)
                    {
                        Check();
                        await context.CreateTimer(context.CurrentUtcDateTime.AddSeconds(10), CancellationToken.None);
                        Check();
                        result = await context.CallActivityAsync<MapOperationResult>(nameof(MapRotationActivities.SyncSingleMapToServer),
                            new SyncMapInput(serverId, names[index], details.GameType));
                    }
                    mapProgress[index] = mapProgress[index] with
                    {
                        Status = result.Success ? "Completed" : "Failed",
                        Error = result.Error
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
            if (formatted.Parts.Count == 0 || formatted.Parts.Any(part => part.Value.Length > 1024) ||
                (!isAacp && formatted.Parts.Count > 1) || (isAacp && formatted.Parts.Count > 10))
            {
                throw new InvalidOperationException("The rotation exceeds the server slot's supported variable limits (1024 characters; one standard variable or ten AACP parts). Shorten the rotation before applying.");
            }

            foreach (var part in formatted.Parts)
            {
                Check();
                Progress(context, action, $"Write and read back {part.VariableName}", "InProgress");
                await context.CallActivityAsync(nameof(MapRotationActivities.WriteAndVerifyConfigVariable),
                    new WriteConfigInput(serverId, details.ConfigFilePath, part.VariableName, part.Value,
                        action == "Deactivate" ? [] :
                        [$"Managed by XtremeIdiots Portal - {details.Title ?? "Map Rotation"}",
                         $"https://portal.xtremeidiots.com/MapRotations/Details/{details.MapRotationId}"]),
                    MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);
            }

            var used = formatted.Parts.Select(part => part.VariableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in RotationVariableNaming.GetOverflowVariableNames(details.ConfigVariableName, used))
            {
                Check();
                await context.CallActivityAsync(nameof(MapRotationActivities.ClearConfigVariableIfPresent),
                    new WriteConfigInput(serverId, details.ConfigFilePath, variable, "", []),
                    MapRotationOrchestrationPolicies.ActivationActivityRetryOptions);
            }

            foreach (var part in formatted.Parts.Concat(RotationVariableNaming.GetOverflowVariableNames(details.ConfigVariableName, used)
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
            await SetState(new UpdateStatusInput(assignmentId,
                DeploymentState: action == "Unassign" || (configuring && ex is not InvalidDataException) ? null : DeploymentState.Failed,
                ActivationState: configuring ? ActivationState.Failed : null,
                LastError: ex.Message, LastErrorAt: context.CurrentUtcDateTime));
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
