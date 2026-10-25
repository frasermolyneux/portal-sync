using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public static class MapRotationGroupWorkflow
{
    [Function(nameof(ApplyMapRotationV3))]
    public static Task<RotationWorkflowResult> ApplyMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<SyncOrchestrationInput>()!.AssignmentId, "Apply");

    [Function(nameof(RepairMapRotationV3))]
    public static Task<RotationWorkflowResult> RepairMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<SyncOrchestrationInput>()!.AssignmentId, "Repair");

    [Function(nameof(VerifyMapRotationV3))]
    public static Task<RotationWorkflowResult> VerifyMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<VerifyOrchestrationInput>()!.AssignmentId, "Verify");

    [Function(nameof(ActivateMapRotationV3))]
    public static Task<RotationWorkflowResult> ActivateMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<ActivateOrchestrationInput>()!.AssignmentId, "Activate");

    [Function(nameof(DeactivateMapRotationV3))]
    public static Task<RotationWorkflowResult> DeactivateMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<DeactivateOrchestrationInput>()!.AssignmentId, "Deactivate");

    [Function(nameof(UnassignMapRotationV3))]
    public static Task<RotationWorkflowResult> UnassignMapRotationV3([OrchestrationTrigger] TaskOrchestrationContext context) =>
        Run(context, context.GetInput<RemoveOrchestrationInput>()!.AssignmentId, "Unassign");

    internal static async Task<RotationWorkflowResult> Run(TaskOrchestrationContext context, Guid assignmentId, string action)
    {
        var cancellation = context.WaitForExternalEvent<object>(MapRotationWorkflow.CancellationEvent);
        var details = await context.CallActivityAsync<RotationDetails>(nameof(MapRotationActivities.GetRotationDetails),
            new GetRotationDetailsInput(assignmentId));
        Progress(context, action, "Waiting for this server's current operation", "InProgress");
        await using var serverLock = await context.Entities.LockEntitiesAsync(
            new EntityInstanceId(nameof(MapRotationServerLock), details.GameServerId.ToString()));
        try
        {
            var legacyConflict = await context.CallActivityAsync<string?>(nameof(LegacyMapRotationDrain.CheckLegacyMapRotationDrain), details.GameServerId);
            if (legacyConflict != null)
            {
                throw new InvalidOperationException(legacyConflict);
            }
            var group = await context.CallActivityAsync<List<RotationDetails>>(nameof(MapRotationActivities.GetConfigurationGroup),
                new GetRotationDetailsInput(assignmentId));
            ValidateGroup(group, details, action);
            if (cancellation.IsCompleted)
            {
                throw new OperationCanceledException("Cancellation requested; no configuration targets will be changed.");
            }
            if (action == "Unassign")
            {
                await context.CallActivityAsync(nameof(MapRotationActivities.UnassignConfigurationGroup), group);
                Progress(context, action, "Server unassigned; map files retained", "Completed");
                return new RotationWorkflowResult(true);
            }
            var result = await RunTargets(context, group, action, cancellation);
            if (!result.Success)
            {
                return result;
            }
            await ConfirmGroup(context, group, assignmentId, action);
            Progress(context, action, group.Count == 1 ? "Server assignment completed" : "Normal rotation and AACP map picker completed", "Completed");
            return new RotationWorkflowResult(true);
        }
        catch (Exception ex)
        {
            context.CreateReplaySafeLogger(nameof(MapRotationGroupWorkflow)).LogError(ex,
                "{Action} failed for configuration group {AssignmentId}", action, assignmentId);
            Progress(context, action, "Server assignment incomplete", "Failed", ex.Message);
            return new RotationWorkflowResult(false, ex.Message);
        }
    }

    private static void ValidateGroup(List<RotationDetails> group, RotationDetails selected, string action)
    {
        if (group.Count == 0 || group.All(member => member.AssignmentId != selected.AssignmentId)
            || group.Any(member => member.GameServerId != selected.GameServerId || member.MapRotationId != selected.MapRotationId))
        {
            throw new InvalidOperationException("The server assignment changed before its server lock was acquired.");
        }
        if (group.Any(member => member.DeploymentState is DeploymentState.Syncing or DeploymentState.Removing or DeploymentState.Removed
            || member.ActivationState is ActivationState.Activating or ActivationState.Deactivating))
        {
            throw new InvalidOperationException("A configuration target is busy or removed. Wait for its recorded result before retrying.");
        }
        if (action == "Unassign" && group.Any(member => member.ActivationState != ActivationState.Inactive))
        {
            throw new InvalidOperationException("Deactivate both configurations before unassigning this server. Map files will be retained.");
        }
    }

    private static async Task<RotationWorkflowResult> RunTargets(TaskOrchestrationContext context,
        List<RotationDetails> group, string action, Task cancellation)
    {
        for (var index = 0; index < group.Count; index++)
        {
            if (cancellation.IsCompleted)
            {
                throw new OperationCanceledException("Cancellation requested; no further configuration targets will be changed.");
            }
            var member = group[index];
            var memberAction = index == 0 ? action : action switch
            {
                "Apply" => "Activate",
                "Repair" => "Verify",
                _ => action
            };
            var label = string.Equals(member.ConfigVariableName, "scr_aacp_maps_1", StringComparison.OrdinalIgnoreCase)
                ? "AACP map picker" : "Rotation";
            Progress(context, action, $"{label}: {memberAction}", "InProgress");
            var result = await MapRotationWorkflow.RunLocked(context, member.AssignmentId,
                member.GameServerId, memberAction, cancellation, member);
            if (!result.Success)
            {
                var error = $"{label}: {result.Error} Other targets may already have completed; review both configuration results.";
                Progress(context, action, "Server assignment incomplete", "Failed", error);
                return new RotationWorkflowResult(false, error);
            }
        }
        return new RotationWorkflowResult(true);
    }

    private static async Task ConfirmGroup(TaskOrchestrationContext context, List<RotationDetails> group,
        Guid assignmentId, string action)
    {
        var currentGroup = await context.CallActivityAsync<List<RotationDetails>>(nameof(MapRotationActivities.GetConfigurationGroup),
            new GetRotationDetailsInput(assignmentId));
        if (!group.Select(member => member.AssignmentId).Order().SequenceEqual(currentGroup.Select(member => member.AssignmentId).Order()))
        {
            throw new InvalidOperationException("The configuration group changed during this action. Review both targets before retrying.");
        }
        foreach (var snapshot in group)
        {
            var current = currentGroup.Single(member => member.AssignmentId == snapshot.AssignmentId);
            var matches = current.RotationVersion == snapshot.RotationVersion
                && current.MapRotationId == snapshot.MapRotationId && current.GameServerId == snapshot.GameServerId
                && current.ConfigFilePath == snapshot.ConfigFilePath && current.ConfigVariableName == snapshot.ConfigVariableName;
            var complete = action switch
            {
                "Apply" or "Activate" => current.ActivationState == ActivationState.Active
                    && current.DeploymentState == DeploymentState.Synced && current.DeployedVersion == snapshot.RotationVersion,
                "Deactivate" => current.ActivationState == ActivationState.Inactive,
                _ => current.DeploymentState == DeploymentState.Synced && current.DeployedVersion == snapshot.RotationVersion
            };
            if (!matches || !complete)
            {
                throw new InvalidOperationException("A configuration target changed or lacks current confirmation. Review the server assignment and retry.");
            }
        }
    }

    private static void Progress(TaskOrchestrationContext context, string action, string step, string state, string? error = null) =>
        context.SetCustomStatus(new OrchestrationProgress(action, 1, state is "Completed" or "Failed" ? 1 : 0,
            [new MapProgress(step, state, error)]));
}
