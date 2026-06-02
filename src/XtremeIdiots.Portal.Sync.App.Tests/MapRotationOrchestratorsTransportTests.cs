using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using Moq;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationOrchestratorsTransportTests
{
    [Fact]
    public async Task VerifyMapRotationOrchestrator_UsesLoadedMapsActivityForTransportNeutralVerification()
    {
        var assignmentId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var gameServerId = Guid.NewGuid();

        var contextMock = new Mock<TaskOrchestrationContext>(MockBehavior.Loose);
        contextMock.Setup(x => x.GetInput<VerifyOrchestrationInput>())
            .Returns(new VerifyOrchestrationInput(assignmentId));
        contextMock.SetupGet(x => x.InstanceId).Returns("verify-transport-instance");

        contextMock.Setup(x => x.CallActivityAsync<Guid>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.RecordOperation)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(operationId);

        contextMock.Setup(x => x.CallActivityAsync<RotationDetails>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.GetRotationDetails)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(new RotationDetails(
                AssignmentId: assignmentId,
                GameServerId: gameServerId,
                MapRotationId: Guid.NewGuid(),
                GameType: GameType.CallOfDuty4,
                DeploymentState: DeploymentState.Synced,
                ActivationState: ActivationState.Active,
                DeployedVersion: 1,
                RotationVersion: 1,
                ContentHash: "hash",
                ConfigFilePath: "main.cfg",
                ConfigVariableName: "sv_maprotation",
                GameMode: "war",
                MapIds: [Guid.NewGuid()]));

        contextMock.Setup(x => x.CallActivityAsync<List<string>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.ResolveMapNames)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(["custom_map_alpha"]);

        contextMock.Setup(x => x.CallActivityAsync<List<string>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.GetLoadedMapsFromServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(["custom_map_alpha"]);

        contextMock.Setup(x => x.CallActivityAsync(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.CompleteOperation)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .Returns(Task.CompletedTask);

        await MapRotationOrchestrators.VerifyMapRotationOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<List<string>>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.GetLoadedMapsFromServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task PushMapToServerOrchestrator_MixedFleetServerIds_RoutesBothThroughSyncActivity()
    {
        var ftpServerId = Guid.NewGuid();
        var sftpServerId = Guid.NewGuid();

        await ExecutePushMapOrchestrator(ftpServerId, GameType.CallOfDuty4);
        await ExecutePushMapOrchestrator(sftpServerId, GameType.CallOfDuty4x);
    }

    private static async Task ExecutePushMapOrchestrator(Guid serverId, GameType gameType)
    {
        var contextMock = new Mock<TaskOrchestrationContext>(MockBehavior.Loose);
        contextMock.Setup(x => x.GetInput<PushMapOrchestrationInput>())
            .Returns(new PushMapOrchestrationInput(serverId, "custom_map_alpha", gameType));
        contextMock.Setup(x => x.CreateReplaySafeLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());

        contextMock.Setup(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
                It.IsAny<object>(),
                It.IsAny<TaskOptions>()))
            .ReturnsAsync(new MapOperationResult("custom_map_alpha", true));

        await MapRotationOrchestrators.PushMapToServerOrchestrator(contextMock.Object);

        contextMock.Verify(x => x.CallActivityAsync<MapOperationResult>(
                It.Is<TaskName>(n => (string)n == nameof(MapRotationActivities.SyncSingleMapToServer)),
            It.Is<SyncMapInput>(i => i.GameServerId == serverId && i.MapName == "custom_map_alpha"),
                It.IsAny<TaskOptions>()),
            Times.Once);
    }
}
