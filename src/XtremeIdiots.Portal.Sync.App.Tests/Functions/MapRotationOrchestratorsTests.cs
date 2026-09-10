using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Moq;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class MapRotationOrchestratorsTests
{
    private static Mock<TaskOrchestrationContext> CreateContext(
        Guid assignmentId,
        Guid gameServerId,
        Guid mapId,
        List<string> mapNames,
        List<string> mapsWithoutFiles,
        List<string> loadedMaps,
        DeploymentState deploymentState = DeploymentState.Synced)
    {
        var contextMock = new Mock<TaskOrchestrationContext>(MockBehavior.Loose);
        contextMock.SetupGet(x => x.InstanceId).Returns("test-instance");
        contextMock.Setup(x => x.CreateReplaySafeLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());

        contextMock.Setup(x => x.CallActivityAsync<Guid>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.RecordOperation)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(Guid.NewGuid());

        contextMock.Setup(x => x.CallActivityAsync<RotationDetails>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.GetRotationDetails)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(new RotationDetails(
                AssignmentId: assignmentId,
                GameServerId: gameServerId,
                MapRotationId: Guid.NewGuid(),
                GameType: GameType.CallOfDuty4,
                DeploymentState: deploymentState,
                ActivationState: ActivationState.Active,
                DeployedVersion: 1,
                RotationVersion: 2,
                ContentHash: "hash",
                ConfigFilePath: "main.cfg",
                ConfigVariableName: "sv_maprotation",
                GameMode: "war",
                MapIds: [mapId]));

        contextMock.Setup(x => x.CallActivityAsync<List<RotationMapDetail>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.ResolveRotationMaps)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(mapNames
                .Select(m => new RotationMapDetail(m, !mapsWithoutFiles.Contains(m, StringComparer.OrdinalIgnoreCase)))
                .ToList());

        contextMock.Setup(x => x.CallActivityAsync<List<string>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.GetLoadedMapsFromServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(loadedMaps);

        contextMock.Setup(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("map", true));

        contextMock.Setup(x => x.CallActivityAsync(
                It.IsAny<TaskName>(),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        return contextMock;
    }

    [Fact]
    public async Task SyncMapRotationOrchestrator_WhenMapHasNoFiles_DoesNotPushMapToHost()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_no_files"],
            mapsWithoutFiles: ["mp_no_files"],
            loadedMaps: []);

        contextMock.Setup(x => x.GetInput<SyncOrchestrationInput>())
            .Returns(new SyncOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.SyncMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()),
            Times.Never);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.UpdateAssignmentStatus)),
                It.Is<UpdateStatusInput>(i => i.DeploymentState == DeploymentState.PartiallyDeployed),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncMapRotationOrchestrator_WhenMapHasFiles_PushesMapToHost()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_custom"],
            mapsWithoutFiles: [],
            loadedMaps: []);

        contextMock.Setup(x => x.GetInput<SyncOrchestrationInput>())
            .Returns(new SyncOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.SyncMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
                It.Is<SyncMapInput>(i => i.MapName == "mp_custom" && !i.Force),
                It.IsAny<TaskOptions>()),
            Times.Once);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.UpdateAssignmentStatus)),
                It.Is<UpdateStatusInput>(i => i.DeploymentState == DeploymentState.Synced),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncMapRotationOrchestrator_WhenForced_PropagatesForceToSyncActivity()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_custom"],
            mapsWithoutFiles: [],
            loadedMaps: []);

        contextMock.Setup(x => x.GetInput<SyncOrchestrationInput>())
            .Returns(new SyncOrchestrationInput(assignmentId, Force: true));

        await MapRotationOrchestrators.SyncMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
                It.Is<SyncMapInput>(i => i.Force),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task SyncMapRotationOrchestrator_ResolvesRotationMapsOnceForNamesAndFileAvailability()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_custom"],
            mapsWithoutFiles: [],
            loadedMaps: []);

        contextMock.Setup(x => x.GetInput<SyncOrchestrationInput>())
            .Returns(new SyncOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.SyncMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<List<RotationMapDetail>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.ResolveRotationMaps)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyMapRotationOrchestrator_WhenMapHasNoFiles_FailsVerificationAndFlagsAssignment()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_no_files"],
            mapsWithoutFiles: ["mp_no_files"],
            // The host reports the (empty) map directory as present
            loadedMaps: ["mp_no_files"]);

        contextMock.Setup(x => x.GetInput<VerifyOrchestrationInput>())
            .Returns(new VerifyOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.VerifyMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.UpdateAssignmentStatus)),
                It.Is<UpdateStatusInput>(i => i.DeploymentState == DeploymentState.PartiallyDeployed
                    && i.LastError != null && i.LastError.Contains("mp_no_files")),
                It.IsAny<TaskOptions>()),
            Times.Once);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.CompleteOperation)),
                It.Is<CompleteOperationInput>(i => i.Status == AssignmentOperationStatus.Failed),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyMapRotationOrchestrator_WhenAllMapsPresent_DoesNotFlagAssignment()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_custom"],
            mapsWithoutFiles: [],
            loadedMaps: ["mp_custom"]);

        contextMock.Setup(x => x.GetInput<VerifyOrchestrationInput>())
            .Returns(new VerifyOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.VerifyMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.UpdateAssignmentStatus)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()),
            Times.Never);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.CompleteOperation)),
                It.Is<CompleteOperationInput>(i => i.Status == AssignmentOperationStatus.Succeeded),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task VerifyMapRotationOrchestrator_WhenPartiallyDeployedAssignmentVerifies_ClearsTheFailureState()
    {
        var assignmentId = Guid.NewGuid();
        var contextMock = CreateContext(
            assignmentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            mapNames: ["mp_custom"],
            mapsWithoutFiles: [],
            loadedMaps: ["mp_custom"],
            deploymentState: DeploymentState.PartiallyDeployed);

        contextMock.Setup(x => x.GetInput<VerifyOrchestrationInput>())
            .Returns(new VerifyOrchestrationInput(assignmentId));

        await MapRotationOrchestrators.VerifyMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.UpdateAssignmentStatus)),
                It.Is<UpdateStatusInput>(i => i.DeploymentState == DeploymentState.Synced && i.LastError == ""),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }
}
