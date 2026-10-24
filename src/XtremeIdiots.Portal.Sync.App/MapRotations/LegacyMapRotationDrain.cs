using DurableTask.Core.History;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public static class LegacyMapRotationDrain
{
    [Function(nameof(CheckLegacyMapRotationDrain))]
    public static Task<string?> CheckLegacyMapRotationDrain(
        [ActivityTrigger] Guid serverId,
        [DurableClient] DurableTaskClient client) => FindConflict(client);

    public static async Task<string?> FindConflict(DurableTaskClient client)
    {
        // V1 histories cannot acquire the V2 server lock. Drain them across the hub before admitting V2 work.
        foreach (var prefix in new[] { "maprot-", "map-push-" })
        {
            var query = new OrchestrationQuery
            {
                InstanceIdPrefix = prefix,
                Statuses =
                [
                    OrchestrationRuntimeStatus.Pending,
                    OrchestrationRuntimeStatus.Running,
                    OrchestrationRuntimeStatus.Suspended,
                    OrchestrationRuntimeStatus.Failed,
                    OrchestrationRuntimeStatus.Terminated
                ],
                PageSize = 100
            };
            await foreach (var instance in client.GetAllInstancesAsync(query).ConfigureAwait(false))
            {
                if (!IsLegacy(instance.Name))
                {
                    continue;
                }
                if (instance.RuntimeStatus is not (OrchestrationRuntimeStatus.Failed or OrchestrationRuntimeStatus.Terminated))
                {
                    return $"Legacy rotation operation {instance.InstanceId} is {instance.RuntimeStatus}. Let legacy work finish before starting new rotation operations.";
                }

                // Terminating an orchestration does not stop its activities. Preserve this evidence until drain is proven.
                var history = await client.GetOrchestrationHistoryAsync(instance.InstanceId).ConfigureAwait(false);
                var finished = history.Select(item => item switch
                {
                    TaskCompletedEvent completed => (int?)completed.TaskScheduledId,
                    TaskFailedEvent failed => failed.TaskScheduledId,
                    _ => null
                }).OfType<int>().ToHashSet();
                if (!history.OfType<ExecutionStartedEvent>().Any() ||
                    history.OfType<TaskScheduledEvent>().Any(item => !finished.Contains(item.EventId)))
                {
                    return $"Legacy rotation operation {instance.InstanceId} has unconfirmed activity completion. Service-side investigation is required before new rotation work; do not purge it or reset assignment state.";
                }
            }
        }
        return null;
    }

    private static bool IsLegacy(string name) => name is
        nameof(MapRotationOrchestrators.SyncMapRotationOrchestrator) or
        nameof(MapRotationOrchestrators.RemoveMapRotationOrchestrator) or
        nameof(MapRotationOrchestrators.ActivateMapRotationOrchestrator) or
        nameof(MapRotationOrchestrators.DeactivateMapRotationOrchestrator) or
        nameof(MapRotationOrchestrators.VerifyMapRotationOrchestrator) or
        nameof(MapRotationOrchestrators.PushMapToServerOrchestrator);
}
