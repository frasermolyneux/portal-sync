using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class MapRotationCleanupTests
{
    private readonly Mock<IRepositoryApiClient> repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IJobTelemetry> jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> auditLoggerMock = new();

    public MapRotationCleanupTests()
    {
        jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    [Fact]
    public async Task RunMapRotationCleanup_WhenAssignmentIsStaleRemoving_ReconcilesToRemoved()
    {
        var assignmentId = Guid.NewGuid();
        var oldUpdatedAt = DateTime.UtcNow.AddMinutes(-20);
        var assignments = new CollectionModel<MapRotationServerAssignmentDto>(
        [
            new MapRotationServerAssignmentDto(
                assignmentId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DeploymentState.Removing,
                ActivationState.Inactive,
                null,
                null,
                "server.cfg",
                "sv_maprotation",
                null,
                null,
                oldUpdatedAt.AddHours(-1),
                oldUpdatedAt,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(assignments)));

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var operations = new CollectionModel<MapRotationAssignmentOperationDto>(
        [
            new MapRotationAssignmentOperationDto(
                Guid.NewGuid(),
                assignmentId,
                AssignmentOperationType.Remove,
                AssignmentOperationStatus.Failed,
                null,
                DateTime.UtcNow.AddHours(-2),
                DateTime.UtcNow.AddHours(-2),
                "previous failure")
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetAssignmentOperations(assignmentId, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(operations)));

        var sut = new MapRotationCleanup(
            Mock.Of<ILogger<MapRotationCleanup>>(),
            repositoryApiClientMock.Object,
            jobTelemetryMock.Object,
            auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Verify(x => x.UpdateServerAssignment(
                It.Is<UpdateMapRotationServerAssignmentDto>(dto =>
                    dto.MapRotationServerAssignmentId == assignmentId
                    && dto.DeploymentState == DeploymentState.Removed
                    && dto.UnassignedAt == oldUpdatedAt),
                It.IsAny<CancellationToken>()),
                Times.Once);
    }

    [Fact]
    public async Task RunMapRotationCleanup_WhenStaleRemovingHasRecentInProgressRemove_DoesNotReconcile()
    {
        var assignmentId = Guid.NewGuid();
        var oldUpdatedAt = DateTime.UtcNow.AddMinutes(-20);
        var assignments = new CollectionModel<MapRotationServerAssignmentDto>(
        [
            new MapRotationServerAssignmentDto(
                assignmentId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DeploymentState.Removing,
                ActivationState.Inactive,
                null,
                null,
                "server.cfg",
                "sv_maprotation",
                null,
                null,
                oldUpdatedAt.AddHours(-1),
                oldUpdatedAt,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(assignments)));

        var operations = new CollectionModel<MapRotationAssignmentOperationDto>(
        [
            new MapRotationAssignmentOperationDto(
                Guid.NewGuid(),
                assignmentId,
                AssignmentOperationType.Remove,
                AssignmentOperationStatus.InProgress,
                "maprot-remove-instance",
                DateTime.UtcNow.AddMinutes(-10),
                null,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetAssignmentOperations(assignmentId, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(operations)));

        var sut = new MapRotationCleanup(
            Mock.Of<ILogger<MapRotationCleanup>>(),
            repositoryApiClientMock.Object,
            jobTelemetryMock.Object,
            auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Verify(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
