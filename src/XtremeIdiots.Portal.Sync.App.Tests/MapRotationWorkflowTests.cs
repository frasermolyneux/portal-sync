using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationWorkflowTests
{
    private readonly Guid assignmentId = Guid.NewGuid();
    private readonly Guid serverId = Guid.NewGuid();
    private readonly Mock<TaskOrchestrationContext> context = new();
    private readonly List<UpdateStatusInput> updates = [];
    private readonly List<CompleteOperationInput> operations = [];
    private readonly RotationDetails details;

    public MapRotationWorkflowTests()
    {
        details = new(assignmentId, serverId, Guid.NewGuid(), GameType.CallOfDuty5,
            DeploymentState.Synced, ActivationState.Active, 1, 1, "hash", "server.cfg",
            "scr_aacp_maps_1", "tdm", [Guid.NewGuid()]);
        context.SetupGet(x => x.InstanceId).Returns("maprot-test");
        context.SetupGet(x => x.CurrentUtcDateTime).Returns(new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc));
        context.Setup(x => x.CreateReplaySafeLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details);
        context.Setup(x => x.CallActivityAsync<Guid>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(Guid.NewGuid());
        context.Setup(x => x.CallActivityAsync<List<string>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(["mp_custom"]);
        context.Setup(x => x.CallActivityAsync<List<MapOperationResult>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([new MapOperationResult("mp_custom", true)]);
        context.Setup(x => x.CallActivityAsync<FormatRotationOutput>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new FormatRotationOutput([new RotationStringPart("scr_aacp_maps_1", "mp_custom")]));
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("mp_custom", true));
        context.Setup(x => x.CallActivityAsync(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Callback<TaskName, object, TaskOptions>((_, input, _) =>
            {
                if (input is UpdateStatusInput update)
                {
                    updates.Add(update);
                }
                if (input is CompleteOperationInput operation)
                {
                    operations.Add(operation);
                }
            }).Returns(Task.CompletedTask);
        context.Setup(x => x.CreateTimer(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ApplyHoldsOneServerLockAcrossLinkedSlotsAndRepairsFilesOnce()
    {
        var linkedId = Guid.NewGuid();
        context.Setup(x => x.GetInput<SyncOrchestrationInput>()).Returns(new SyncOrchestrationInput(assignmentId));
        context.Setup(x => x.WaitForExternalEvent<object>(MapRotationWorkflow.CancellationEvent, It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<object>().Task);
        var entities = new Mock<TaskOrchestrationEntityFeature>();
        var lease = new Mock<IAsyncDisposable>();
        entities.Setup(x => x.LockEntitiesAsync(It.IsAny<EntityInstanceId[]>())).ReturnsAsync(lease.Object);
        context.SetupGet(x => x.Entities).Returns(entities.Object);
        context.Setup(x => x.CallActivityAsync<List<Guid>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([linkedId]);
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
            It.Is<GetRotationDetailsInput>(input => input.AssignmentId == linkedId), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { AssignmentId = linkedId, ConfigVariableName = "sv_maprotation" });

        var result = await MapRotationWorkflow.ApplyMapRotationV2(context.Object);

        Assert.True(result.Success);
        entities.Verify(x => x.LockEntitiesAsync(It.Is<EntityInstanceId[]>(ids =>
            ids.Single().Key == serverId.ToString())), Times.Once);
        lease.Verify(x => x.DisposeAsync(), Times.Once);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Once);
        Assert.Contains(updates, update => update.AssignmentId == linkedId && update.ActivationState == ActivationState.Active);
    }

    [Fact]
    public async Task ChangedRotationSnapshotStopsConfigurationWrites()
    {
        context.SetupSequence(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details)
            .ReturnsAsync(details with { RotationVersion = 2 });

        var result = await Run("Activate");

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task RepairAacpActuallyTransfersAndVerifiesFilesWithoutChangingConfiguration()
    {
        var result = await Run("Repair");
        Assert.True(result.Success);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
            It.Is<SyncMapInput>(input => input.MapName == "mp_custom"), It.IsAny<TaskOptions>()), Times.Once);
        Assert.Contains(updates, update => update.DeploymentState == DeploymentState.Synced && update.DeployedVersion == 1);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        Assert.All(operations, operation => Assert.Equal(AssignmentOperationStatus.Succeeded, operation.Status));
    }

    [Fact]
    public async Task VerifyRejectsEmptyFoldersAndInvalidatesDeploymentEvidence()
    {
        context.Setup(x => x.CallActivityAsync<List<MapOperationResult>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([new MapOperationResult("mp_custom", false, "Required file missing")]);

        var result = await Run("Verify");

        Assert.False(result.Success);
        Assert.Contains("mp_custom", result.Error);
        Assert.Contains(updates, update => update.DeploymentState == DeploymentState.Failed);
        Assert.Equal(AssignmentOperationStatus.Failed, Assert.Single(operations).Status);
    }

    [Fact]
    public async Task ApplyDoesNotWriteConfigurationWhenRepairFails()
    {
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("mp_custom", false, "Transfer failed"));

        var result = await Run("Apply");

        Assert.False(result.Success);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Exactly(2));
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        Assert.DoesNotContain(updates, update => update.ActivationState == ActivationState.Active);
    }

    [Fact]
    public async Task OverflowClearFailurePreventsSuccessfulActivation()
    {
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.Is<SetRconDvarInput>(input => input.Value == ""), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("scr_aacp_maps_2", false, "RCON rejected clear"));

        var result = await Run("Activate");

        Assert.False(result.Success);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Failed);
        Assert.DoesNotContain(updates, update => update.ActivationState == ActivationState.Active);
        Assert.Equal(AssignmentOperationStatus.Failed, Assert.Single(operations).Status);
    }

    [Fact]
    public async Task CancellationWaitsForTransferThenPreventsFurtherServerChanges()
    {
        var cancelled = new TaskCompletionSource();
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Callback(() => cancelled.SetResult())
            .ReturnsAsync(new MapOperationResult("mp_custom", true));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Apply", cancelled.Task);

        Assert.False(result.Success);
        Assert.Equal(AssignmentOperationStatus.Cancelled, Assert.Single(operations).Status);
        context.Verify(x => x.CallActivityAsync<List<MapOperationResult>>(It.IsAny<TaskName>(),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        Assert.DoesNotContain(updates, update => update.DeploymentState == DeploymentState.Synced);
    }

    [Theory]
    [InlineData(ActivationState.Active, false)]
    [InlineData(ActivationState.Inactive, true)]
    public async Task UnassignRequiresInactiveConfigurationAndNeverDeletesMaps(ActivationState state, bool success)
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ActivationState = state });

        var result = await Run("Unassign");

        Assert.Equal(success, result.Success);
        Assert.Equal(success, updates.Any(update => update.DeploymentState == DeploymentState.Removed));
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    private Task<RotationWorkflowResult> Run(string action) =>
        MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, action, new TaskCompletionSource().Task);
}
