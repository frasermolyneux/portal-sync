using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Config;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
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
            .ReturnsAsync((TaskName _, object input, TaskOptions _) => new FormatRotationOutput(
                [new RotationStringPart(((FormatRotationInput)input).ConfigVariableName, "mp_custom")]));
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("mp_custom", true));
        context.Setup(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigWriteResult(true));
        context.Setup(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigPreflightResult(true));
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

    [Theory]
    [InlineData("mp_bog")]
    [InlineData("mp_farm")]
    [InlineData("mp_convoy")]
    [InlineData("mp_citystreets")]
    [InlineData("mp_carentan")]
    [InlineData("mp_crash_snow")]
    public async Task Cod4xApplySkipsStockTransfersRepairsCustomMapsAndActivatesUsingNativeRcon(string stockMap)
    {
        var servers = new Mock<IServersApiClient> { DefaultValue = DefaultValue.Mock };
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(), Mock.Of<IRepositoryApiClient>(), servers.Object);
        var uploaded = false;
        var repairs = new List<MapOperationResult>();
        servers.Setup(x => x.Maps.V1.VerifyServerMaps(serverId, It.IsAny<List<string>>()))
            .ReturnsAsync((Guid _, List<string> names, CancellationToken _) => new ApiResult<MapVerificationCollectionDto>(
                System.Net.HttpStatusCode.OK, new ApiResponse<MapVerificationCollectionDto>(
                    new MapVerificationCollectionDto(names.Select(name => new MapVerificationResultDto(
                        name, name == stockMap || uploaded)).ToList()))));
        servers.Setup(x => x.Maps.V1.PushServerMapToHost(serverId, "mp_custom"))
            .Callback(() => uploaded = true)
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK, new ApiResponse()));
        servers.Setup(x => x.CoD4xRcon.V1.Set(serverId, It.IsAny<CoD4xSetDvarRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(System.Net.HttpStatusCode.OK, new ApiResponse<string>("ok")));
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { GameType = GameType.CallOfDuty4x, ConfigVariableName = "sv_maprotation" });
        context.Setup(x => x.CallActivityAsync<List<string>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([stockMap, "mp_custom"]);
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(async (TaskName _, object input, TaskOptions _) =>
            {
                var result = await activities.RepairSingleMapToServer((SyncMapInput)input);
                repairs.Add(result);
                return result;
            });
        context.Setup(x => x.CallActivityAsync<List<MapOperationResult>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns((TaskName _, object input, TaskOptions _) => activities.VerifyMapFiles((VerifyMapFilesInput)input));
        context.Setup(x => x.CallActivityAsync<FormatRotationOutput>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns((TaskName _, object input, TaskOptions _) => activities.FormatRotationString((FormatRotationInput)input));
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns((TaskName _, object input, TaskOptions _) => activities.SetRconDvar((SetRconDvarInput)input));

        var result = await Run("Apply");

        Assert.True(result.Success, result.Error);
        Assert.Equal(SkipReasons.BuiltInMap, repairs.Single(repair => repair.MapName == stockMap).SkipReason);
        Assert.True(uploaded);
        servers.Verify(x => x.Maps.V1.PushServerMapToHost(serverId, "mp_custom"), Times.Once);
        servers.Verify(x => x.Maps.V1.PushServerMapToHost(serverId, stockMap), Times.Never);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.Is<WriteConfigInput>(input => input.Value == $"gametype tdm map {stockMap} map mp_custom"),
            It.IsAny<TaskOptions>()), Times.Once);
        servers.Verify(x => x.CoD4xRcon.V1.Set(serverId,
            It.Is<CoD4xSetDvarRequestDto>(request => request.DvarName == "sv_maprotation" &&
                request.Value == $"gametype tdm map {stockMap} map mp_custom"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains(updates, update => update.DeploymentState == DeploymentState.Synced);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupApplyRepairsOnceAndConfirmsBothTargetsUnderOneServerLock(bool recoverable)
    {
        var (normalId, entities, lease) = SetupGroup();

        var result = recoverable
            ? await MapRotationGroupWorkflow.ApplyMapRotationV4(context.Object)
            : await MapRotationGroupWorkflow.ApplyMapRotationV3(context.Object);

        Assert.True(result.Success);
        entities.Verify(x => x.LockEntitiesAsync(It.Is<EntityInstanceId[]>(ids =>
            ids.Single().Key == serverId.ToString())), Times.Once);
        lease.Verify(x => x.DisposeAsync(), Times.Once);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Once);
        Assert.Contains(updates, update => update.AssignmentId == normalId && update.ActivationState == ActivationState.Active);
        Assert.Contains(updates, update => update.AssignmentId == assignmentId && update.ActivationState == ActivationState.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupApplyReportsPartialFailureWithoutHidingSuccessfulNormalTarget(bool recoverable)
    {
        var (normalId, _, _) = SetupGroup();
        context.Setup(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.Is<SetRconDvarInput>(input => input.DvarName == "scr_aacp_maps_1"), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("scr_aacp_maps_1", false, "Picker command failed"));

        var result = recoverable
            ? await MapRotationGroupWorkflow.ApplyMapRotationV4(context.Object)
            : await MapRotationGroupWorkflow.ApplyMapRotationV3(context.Object);

        Assert.False(result.Success);
        Assert.Contains("AACP", result.Error);
        Assert.Contains(updates, update => update.AssignmentId == normalId && update.ActivationState == ActivationState.Active);
        Assert.Contains(updates, update => update.AssignmentId == assignmentId && update.ActivationState == ActivationState.Failed);
    }

    [Theory]
    [InlineData("Repair")]
    [InlineData("Deactivate")]
    public async Task GroupActionsCoordinateBothTargetsWithoutDuplicateFileRepair(string action)
    {
        var (normalId, _, _) = SetupGroup();

        var result = await MapRotationGroupWorkflow.Run(context.Object, assignmentId, action);

        Assert.True(result.Success);
        if (action == "Repair")
        {
            context.Verify(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(name => name == nameof(MapRotationActivities.RepairSingleMapToServer)),
                It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Once);
            Assert.Contains(updates, update => update.AssignmentId == normalId && update.DeploymentState == DeploymentState.Synced);
            Assert.Contains(updates, update => update.AssignmentId == assignmentId && update.DeploymentState == DeploymentState.Synced);
        }
        else
        {
            Assert.Contains(updates, update => update.AssignmentId == normalId && update.ActivationState == ActivationState.Inactive);
            Assert.Contains(updates, update => update.AssignmentId == assignmentId && update.ActivationState == ActivationState.Inactive);
        }
    }

    [Fact]
    public async Task GroupUnassignRejectsAnyActiveTargetBeforeMutatingEither()
    {
        _ = SetupGroup();

        var result = await MapRotationGroupWorkflow.Run(context.Object, assignmentId, "Unassign");

        Assert.False(result.Success);
        Assert.Empty(updates);
        context.Verify(x => x.CallActivityAsync(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task GroupUnassignUsesOneAtomicMetadataActionWithoutChangingFiles()
    {
        var (normalId, _, _) = SetupGroup();
        var inactive = details with { ActivationState = ActivationState.Inactive };
        context.Setup(x => x.CallActivityAsync<List<RotationDetails>>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.GetConfigurationGroup)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([inactive with { AssignmentId = normalId, ConfigVariableName = "sv_maprotation" }, inactive]);

        var result = await MapRotationGroupWorkflow.Run(context.Object, assignmentId, "Unassign");

        Assert.True(result.Success);
        Assert.Empty(updates);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.UnassignConfigurationGroup)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task GroupCancellationBeforeExecutionLeavesBothTargetsUntouched()
    {
        _ = SetupGroup();
        context.Setup(x => x.WaitForExternalEvent<object>("Cancel", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new object());

        var result = await MapRotationGroupWorkflow.Run(context.Object, assignmentId, "Apply");

        Assert.False(result.Success);
        Assert.Empty(updates);
        Assert.Empty(operations);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(It.IsAny<TaskName>(),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task ApplyHoldsOneServerLockAcrossLinkedSlotsAndRepairsFilesOnce()
    {
        var (linkedId, entities, lease) = SetupLinkedApply();

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
    public async Task ApplyRejectsUndrainedLegacyWorkWithoutRecordingOrMutatingAssignments()
    {
        _ = SetupLinkedApply();
        context.Setup(x => x.CallActivityAsync<string?>(
            It.Is<TaskName>(name => name == nameof(LegacyMapRotationDrain.CheckLegacyMapRotationDrain)),
            It.IsAny<object?>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync("Legacy operation is still running.");

        var result = await MapRotationWorkflow.ApplyMapRotationV2(context.Object);

        Assert.False(result.Success);
        Assert.Contains("Legacy", result.Error);
        Assert.Empty(updates);
        context.Verify(x => x.CallActivityAsync<Guid>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.RecordOperation)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task MapPushChecksLegacyDrainBeforeStartingAFileTransfer()
    {
        _ = SetupLinkedApply();
        context.Setup(x => x.GetInput<PushMapOrchestrationInput>())
            .Returns(new PushMapOrchestrationInput(serverId, "mp_custom", GameType.CallOfDuty5));
        context.Setup(x => x.CallActivityAsync<string?>(
            It.Is<TaskName>(name => name == nameof(LegacyMapRotationDrain.CheckLegacyMapRotationDrain)),
            It.IsAny<object?>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync("Legacy operation has unconfirmed activity completion.");

        var result = await MapRotationWorkflow.PushMapToServerV2(context.Object);

        Assert.False(result.Success);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SyncSingleMapToServer)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApplyRejectsLinkedVersionOrTargetDriftBeforeWritingThatSlot(bool changesVersion)
    {
        var (linkedId, _, _) = SetupLinkedApply();
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
            It.Is<GetRotationDetailsInput>(input => input.AssignmentId == linkedId), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with
            {
                AssignmentId = linkedId,
                RotationVersion = changesVersion ? 2 : 1,
                ConfigVariableName = changesVersion ? "sv_maprotation" : "scr_small_rotation"
            });

        var result = await MapRotationWorkflow.ApplyMapRotationV2(context.Object);

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.Is<WriteConfigInput>(input => input.ConfigVariableName != "scr_aacp_maps_1"),
            It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task ApplyRevalidatesTheSelectedSlotAfterApplyingTheCounterpart()
    {
        _ = SetupLinkedApply();
        context.Setup(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.Is<WriteConfigInput>(input => input.ConfigVariableName == "sv_maprotation"), It.IsAny<TaskOptions>()))
            .Callback(() => context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
                It.Is<GetRotationDetailsInput>(input => input.AssignmentId == assignmentId), It.IsAny<TaskOptions>()))
                .ReturnsAsync(details with { RotationVersion = 2 }))
            .Returns(Task.CompletedTask);

        var result = await MapRotationWorkflow.ApplyMapRotationV2(context.Object);

        Assert.False(result.Success);
        Assert.Contains("changed", result.Error);
    }

    [Theory]
    [InlineData("scr_dm3_rotation1")]
    [InlineData("scr_dm3_rotation01")]
    public async Task NumberedCustomSlotNeverWritesOrClearsIndependentSiblingVariables(string variable)
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ConfigVariableName = variable });
        context.Setup(x => x.CallActivityAsync<FormatRotationOutput>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new FormatRotationOutput([new RotationStringPart("scr_dm3_rotation1", "mp_custom")]));

        var result = await Run("Activate");

        Assert.True(result.Success);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.ClearConfigVariableIfPresent)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.Is<WriteConfigInput>(input => input.ConfigVariableName != variable), It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.Is<SetRconDvarInput>(input => input.DvarName != variable && input.DvarName != "g_gametype"),
            It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("scr_aacp_maps_0")]
    [InlineData("scr_aacp_maps_01")]
    [InlineData("scr_aacp_maps_11")]
    public async Task UnsupportedAacpVariablesCannotPretendToActivate(string variable)
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ConfigVariableName = variable });

        var result = await Run("Activate");

        Assert.False(result.Success);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData(false, "Activate")]
    [InlineData(true, "Activate")]
    [InlineData(false, "Deactivate")]
    public async Task AacpContinuationsCannotClearOrOverwriteAnotherSlotsVariable(bool needsReservedPart, string action)
    {
        const string reserved = "scr_aacp_maps_2";
        context.Setup(x => x.CallActivityAsync<List<string>>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.GetReservedConfigVariables)),
            It.IsAny<object>(), It.IsAny<TaskOptions>())).ReturnsAsync([reserved]);
        if (needsReservedPart)
        {
            context.Setup(x => x.CallActivityAsync<FormatRotationOutput>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
                .ReturnsAsync(new FormatRotationOutput([
                    new RotationStringPart("scr_aacp_maps_1", "mp_custom"), new RotationStringPart(reserved, "mp_more")]));
        }

        var result = await Run(action);

        Assert.Equal(!needsReservedPart, result.Success);
        context.Verify(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.ClearConfigVariableIfPresent)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.Is<SetRconDvarInput>(input => input.DvarName != "scr_aacp_maps_1"), It.IsAny<TaskOptions>()), Times.Never);
        if (needsReservedPart)
        {
            context.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(name => name == nameof(MapRotationActivities.WriteAndVerifyConfigVariable)),
                It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
        }
    }

    [Fact]
    public async Task ApplyDoesNotFollowAMovedSelectedSlotOntoAnUnlockedServer()
    {
        var (linkedId, _, _) = SetupLinkedApply();
        context.Setup(x => x.CallActivityAsync(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.CompleteOperation)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Callback<TaskName, object, TaskOptions>((_, input, _) =>
            {
                operations.Add((CompleteOperationInput)input);
                if (operations.Count == 2)
                {
                    context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
                        It.Is<GetRotationDetailsInput>(detailsInput => detailsInput.AssignmentId == assignmentId), It.IsAny<TaskOptions>()))
                        .ReturnsAsync(details with { GameServerId = Guid.NewGuid() });
                }
            }).Returns(Task.CompletedTask);

        var result = await MapRotationWorkflow.ApplyMapRotationV2(context.Object);

        Assert.False(result.Success);
        context.Verify(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
            It.Is<GetRotationDetailsInput>(input => input.AssignmentId == linkedId), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Theory]
    [InlineData("Repair")]
    [InlineData("Apply")]
    [InlineData("Verify")]
    [InlineData("Activate")]
    [InlineData("Deactivate")]
    [InlineData("Unassign")]
    public async Task RejectedRemovedTargetRetainsItsExistingAssignmentState(string action)
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { DeploymentState = DeploymentState.Removed, ActivationState = ActivationState.Inactive });

        var result = await Run(action);

        Assert.False(result.Success);
        Assert.Empty(updates);
        Assert.Equal(AssignmentOperationStatus.Failed, Assert.Single(operations).Status);
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

    [Theory]
    [InlineData(ActivationState.Inactive)]
    [InlineData(ActivationState.Active)]
    [InlineData(ActivationState.Failed)]
    public async Task DefinitivePreflightRejectionPreservesActivationStateAndRecordsFailure(ActivationState state)
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ActivationState = state });
        context.Setup(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigPreflightResult(false, "Choose a file with an active rotation declaration."));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.Contains("active rotation declaration", result.Error);
        Assert.All(updates, update => Assert.Null(update.ActivationState));
        Assert.Contains(updates, update => update.LastError == result.Error);
        Assert.Equal(AssignmentOperationStatus.Failed, Assert.Single(operations).Status);
        context.Verify(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task PreWriteFormattingRejectionDoesNotInventAnActivationFailure()
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ActivationState = ActivationState.Inactive, ConfigVariableName = "scr_aacp_maps_11" });

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.All(updates, update => Assert.Null(update.ActivationState));
        context.Verify(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task LaterDefinitiveRejectionCannotEraseAnEarlierUncertainWrite()
    {
        context.SetupSequence(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigWriteResult(false, "Connection lost during upload."))
            .ReturnsAsync(new ConfigWriteResult(false, "Declaration not found."));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Failed);
        context.Verify(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<TaskOptions>()), Times.Exactly(2));
        context.Verify(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<TaskOptions>()), Times.Once);
    }

    [Fact]
    public async Task PartialAacpWriteRemainsProtectedWhenTheNextPartIsRejected()
    {
        context.Setup(x => x.CallActivityAsync<FormatRotationOutput>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new FormatRotationOutput([
                new RotationStringPart("scr_aacp_maps_1", "mp_custom"),
                new RotationStringPart("scr_aacp_maps_2", "mp_more")]));
        context.SetupSequence(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigPreflightResult(true))
            .ReturnsAsync(new ConfigPreflightResult(false, "Second declaration not found."));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Failed);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task LostActivityOutcomeIsNotProofThatConfigurationWasUnchanged()
    {
        context.Setup(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ThrowsAsync(new InvalidOperationException("Activity outcome unavailable."));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Failed);
    }

    [Fact]
    public async Task RedeliveryAfterAnUploadCannotMakeTheAssignmentEditable()
    {
        var servers = new Mock<IServersApiClient> { DefaultValue = DefaultValue.Mock };
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(), Mock.Of<IRepositoryApiClient>(), servers.Object);
        var writes = 0;
        var reads = 0;
        servers.Setup(x => x.Config.V1.UpdateConfigVariable(serverId, details.ConfigFilePath!,
            details.ConfigVariableName!, It.IsAny<string>(), It.IsAny<string[]>()))
            .ReturnsAsync(() => ++writes == 1
                ? new ApiResult(System.Net.HttpStatusCode.OK)
                : new ApiResult(System.Net.HttpStatusCode.BadRequest,
                    new ApiResponse(new ApiError("CONFIG_VARIABLE_NOT_FOUND", "Declaration no longer available."))));
        servers.Setup(x => x.Config.V1.GetConfigFile(serverId, details.ConfigFilePath!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ++reads == 1
                ? new ApiResult<ConfigFileContentDto>(System.Net.HttpStatusCode.OK,
                    new ApiResponse<ConfigFileContentDto>(new ConfigFileContentDto(details.ConfigFilePath!,
                        $"set {details.ConfigVariableName} \"mp_custom\"")))
                : throw new TimeoutException("Read unavailable after the upload."));
        context.Setup(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .Returns(async (TaskName _, object input, TaskOptions _) =>
            {
                _ = await activities.TryWriteAndVerifyConfigVariable((WriteConfigInput)input);
                return await activities.TryWriteAndVerifyConfigVariable((WriteConfigInput)input);
            });

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.True(writes >= 2);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesStateOnlyWhenTheFinishedCallDefinitelyDidNotWrite(bool mayHaveChanged)
    {
        var cancellation = new TaskCompletionSource();
        if (mayHaveChanged)
        {
            context.Setup(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
                .Callback(() => cancellation.SetResult())
                .ReturnsAsync(new ConfigWriteResult(true));
        }
        else
        {
            context.Setup(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
                .Callback(() => cancellation.SetResult())
                .ReturnsAsync(new ConfigPreflightResult(false, "Rejected before write."));
        }

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Activate",
            cancellation.Task, preservePreWriteState: true);

        Assert.False(result.Success);
        Assert.Equal(mayHaveChanged, updates.Any(update => update.ActivationState == ActivationState.Failed));
        Assert.Equal(AssignmentOperationStatus.Cancelled, Assert.Single(operations).Status);
    }

    [Fact]
    public async Task DeactivateCanRecoverAFailedAssignmentWhoseConfigDeclarationIsAlreadyAbsent()
    {
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { ActivationState = ActivationState.Failed, ConfigVariableName = "sv_maprotation" });
        context.Setup(x => x.CallActivityAsync<ConfigPreflightResult>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(new ConfigPreflightResult(false));

        var result = await MapRotationWorkflow.RunLocked(context.Object, assignmentId, serverId, "Deactivate",
            new TaskCompletionSource().Task, preservePreWriteState: true);

        Assert.True(result.Success, result.Error);
        Assert.Contains(updates, update => update.ActivationState == ActivationState.Inactive);
        context.Verify(x => x.CallActivityAsync<ConfigPreflightResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.PreflightConfigVariableForWrite)),
            It.Is<ConfigPreflightInput>(input => input.AllowMissing), It.IsAny<TaskOptions>()), Times.Once);
        context.Verify(x => x.CallActivityAsync<ConfigWriteResult>(It.IsAny<TaskName>(), It.IsAny<object>(),
            It.IsAny<TaskOptions>()), Times.Never);
        context.Verify(x => x.CallActivityAsync<MapOperationResult>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.SetRconDvar)),
            It.Is<SetRconDvarInput>(input => input.DvarName == "sv_maprotation" && input.Value == ""),
            It.IsAny<TaskOptions>()), Times.Once);
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

    private (Guid LinkedId, Mock<TaskOrchestrationEntityFeature> Entities, Mock<IAsyncDisposable> Lease) SetupGroup()
    {
        var result = SetupLinkedApply();
        var normal = details with { AssignmentId = result.LinkedId, ConfigVariableName = "sv_maprotation" };
        RotationDetails Current(RotationDetails snapshot)
        {
            var applied = updates.Where(update => update.AssignmentId == snapshot.AssignmentId).ToList();
            return snapshot with
            {
                DeploymentState = applied.LastOrDefault(update => update.DeploymentState.HasValue)?.DeploymentState ?? snapshot.DeploymentState,
                ActivationState = applied.LastOrDefault(update => update.ActivationState.HasValue)?.ActivationState ?? snapshot.ActivationState,
                DeployedVersion = applied.LastOrDefault(update => update.DeployedVersion.HasValue)?.DeployedVersion ?? snapshot.DeployedVersion
            };
        }

        context.Setup(x => x.CallActivityAsync<List<RotationDetails>>(
            It.Is<TaskName>(name => name == nameof(MapRotationActivities.GetConfigurationGroup)),
            It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync(() => [Current(normal), Current(details)]);
        return result;
    }

    private (Guid LinkedId, Mock<TaskOrchestrationEntityFeature> Entities, Mock<IAsyncDisposable> Lease) SetupLinkedApply()
    {
        var linkedId = Guid.NewGuid();
        context.Setup(x => x.GetInput<SyncOrchestrationInput>()).Returns(new SyncOrchestrationInput(assignmentId));
        context.Setup(x => x.WaitForExternalEvent<object>(MapRotationWorkflow.CancellationEvent, It.IsAny<CancellationToken>()))
            .Returns(new TaskCompletionSource<object>().Task);
        var entities = new Mock<TaskOrchestrationEntityFeature>();
        var lease = new Mock<IAsyncDisposable>();
        entities.Setup(x => x.LockEntitiesAsync(It.IsAny<EntityInstanceId[]>())).ReturnsAsync(lease.Object);
        context.SetupGet(x => x.Entities).Returns(entities.Object);
        context.Setup(x => x.CallActivityAsync<List<LinkedConfigurationTarget>>(It.IsAny<TaskName>(), It.IsAny<object>(), It.IsAny<TaskOptions>()))
            .ReturnsAsync([new LinkedConfigurationTarget(linkedId, "server.cfg", "sv_maprotation")]);
        context.Setup(x => x.CallActivityAsync<RotationDetails>(It.IsAny<TaskName>(),
            It.Is<GetRotationDetailsInput>(input => input.AssignmentId == linkedId), It.IsAny<TaskOptions>()))
            .ReturnsAsync(details with { AssignmentId = linkedId, ConfigVariableName = "sv_maprotation" });
        return (linkedId, entities, lease);
    }
}
