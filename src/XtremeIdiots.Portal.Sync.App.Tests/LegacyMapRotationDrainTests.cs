using DurableTask.Core.History;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Moq;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class LegacyMapRotationDrainTests
{
    [Theory]
    [InlineData(OrchestrationRuntimeStatus.Pending)]
    [InlineData(OrchestrationRuntimeStatus.Running)]
    [InlineData(OrchestrationRuntimeStatus.Suspended)]
    public async Task BlocksLegacyWorkOnAnotherSlotOrServerAcrossQueryPages(OrchestrationRuntimeStatus status)
    {
        var client = CreateClient(
            new OrchestrationMetadata(nameof(MapRotationWorkflow.ApplyMapRotationV2), "maprot-apply-other")
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Running
            },
            new OrchestrationMetadata(nameof(MapRotationOrchestrators.ActivateMapRotationOrchestrator), "maprot-activate-legacy")
            {
                RuntimeStatus = status
            });

        var conflict = await LegacyMapRotationDrain.FindConflict(client.Object);

        Assert.Contains("maprot-activate-legacy", conflict);
    }

    [Theory]
    [InlineData(OrchestrationRuntimeStatus.Terminated, false)]
    [InlineData(OrchestrationRuntimeStatus.Failed, false)]
    [InlineData(OrchestrationRuntimeStatus.Terminated, true)]
    public async Task TerminalStatusDoesNotProveAnOutstandingActivityFinished(OrchestrationRuntimeStatus status, bool failedTask)
    {
        var client = CreateClient(new OrchestrationMetadata(
            nameof(MapRotationOrchestrators.PushMapToServerOrchestrator), "map-push-legacy")
        {
            RuntimeStatus = status
        });
        List<HistoryEvent> history =
        [
            new ExecutionStartedEvent(0, "{}"),
            new TaskScheduledEvent(1),
            failedTask ? new TaskFailedEvent(3, 1, "failure", null) : new TaskCompletedEvent(3, 1, "{}"),
            new TaskScheduledEvent(4)
        ];
        client.Setup(x => x.GetOrchestrationHistoryAsync("map-push-legacy", It.IsAny<CancellationToken>()))
            .ReturnsAsync(history);

        var conflict = await LegacyMapRotationDrain.FindConflict(client.Object);

        Assert.Contains("unconfirmed activity", conflict);
        Assert.DoesNotContain(client.Invocations, call => call.Method.Name == nameof(DurableTaskClient.PurgeInstanceAsync));
    }

    [Fact]
    public async Task AllowsTerminalLegacyOnlyWithMatchingActivityCompletionEvidence()
    {
        var client = CreateClient(new OrchestrationMetadata(
            nameof(MapRotationOrchestrators.ActivateMapRotationOrchestrator), "maprot-activate-legacy")
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Failed
        });
        client.Setup(x => x.GetOrchestrationHistoryAsync("maprot-activate-legacy", It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ExecutionStartedEvent(0, "{}"),
                new TaskScheduledEvent(1),
                new TaskCompletedEvent(2, 1, "{}"),
                new TaskScheduledEvent(3),
                new TaskFailedEvent(4, 3, "failure", null)
            ]);

        Assert.Null(await LegacyMapRotationDrain.FindConflict(client.Object));
    }

    [Fact]
    public async Task MissingHistoryCannotBeMistakenForDrain()
    {
        var client = CreateClient(new OrchestrationMetadata(
            nameof(MapRotationOrchestrators.SyncMapRotationOrchestrator), "maprot-sync-legacy")
        {
            RuntimeStatus = OrchestrationRuntimeStatus.Terminated
        });
        client.Setup(x => x.GetOrchestrationHistoryAsync("maprot-sync-legacy", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Assert.Contains("unconfirmed activity", await LegacyMapRotationDrain.FindConflict(client.Object));
    }

    internal static Mock<DurableTaskClient> CreateClient(params OrchestrationMetadata[] instances)
    {
        var client = new Mock<DurableTaskClient>("tests");
        client.Setup(x => x.GetAllInstancesAsync(It.IsAny<OrchestrationQuery>()))
            .Returns((OrchestrationQuery query) => new InstancePages(
                instances.Where(instance => instance.InstanceId.StartsWith(query.InstanceIdPrefix!, StringComparison.Ordinal)).ToArray()));
        return client;
    }

    private sealed class InstancePages(OrchestrationMetadata[] instances) : AsyncPageable<OrchestrationMetadata>
    {
        public override async IAsyncEnumerable<Page<OrchestrationMetadata>> AsPages(string? continuationToken = null, int? pageSizeHint = null)
        {
            await Task.CompletedTask;
            foreach (var instance in instances)
            {
                yield return new Page<OrchestrationMetadata>([instance], null);
            }
        }
    }
}
