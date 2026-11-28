using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationActivitiesStatusTests
{
    private readonly Mock<IRepositoryApiClient> repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };

    [Fact]
    public async Task UpdateAssignmentStatus_WhenApiReturnsNonSuccess_Throws()
    {
        var id = Guid.NewGuid();
        repositoryApiClientMock.Setup(x => x.MapRotations.V1.GetServerAssignment(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(System.Net.HttpStatusCode.OK,
                new ApiResponse<MapRotationServerAssignmentDto>(Assignment(id, RotationSource.PortalManaged))));
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.BadRequest));

        var sut = new MapRotationActivities(
            Mock.Of<ILogger<MapRotationActivities>>(),
            repositoryApiClientMock.Object,
            Mock.Of<IServersApiClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateAssignmentStatus(new UpdateStatusInput(id, DeploymentState: DeploymentState.Removed)));
        repositoryApiClientMock.Verify(x => x.MapRotations.V1.UpdateServerAssignment(
            It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Details")]
    [InlineData("Status")]
    [InlineData("Operation")]
    [InlineData("Unassign")]
    public async Task ObservedAssignmentsRejectEveryManagedEntryPointBeforeMutation(string action)
    {
        var id = Guid.NewGuid();
        var assignment = Assignment(id, RotationSource.ServerObserved);
        repositoryApiClientMock.Setup(x => x.MapRotations.V1.GetServerAssignment(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(System.Net.HttpStatusCode.OK,
                new ApiResponse<MapRotationServerAssignmentDto>(assignment)));
        var servers = new Mock<IServersApiClient> { DefaultValue = DefaultValue.Mock };
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(),
            repositoryApiClientMock.Object, servers.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            "Details" => activities.GetRotationDetails(new(id)),
            "Status" => activities.UpdateAssignmentStatus(new(id, DeploymentState: DeploymentState.Syncing)),
            "Operation" => activities.RecordOperation(new(id, AssignmentOperationType.Activate)),
            "Unassign" => activities.UnassignConfigurationGroup([new(id, assignment.GameServerId,
                assignment.MapRotationId, GameType.CallOfDuty4, DeploymentState.Removed, ActivationState.Inactive,
                null, 1, null, null, null, null, [])]),
            _ => throw new InvalidOperationException("Unknown test action")
        });

        Assert.Contains("read-only", error.Message);
        repositoryApiClientMock.Verify(x => x.MapRotations.V1.UpdateServerAssignment(
            It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()), Times.Never);
        repositoryApiClientMock.Verify(x => x.MapRotations.V1.CreateAssignmentOperation(
            It.IsAny<CreateMapRotationAssignmentOperationDto>(), It.IsAny<CancellationToken>()), Times.Never);
        repositoryApiClientMock.Verify(x => x.MapRotations.V1.DeleteServerAssignment(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        servers.VerifyNoOtherCalls();
    }

    private static MapRotationServerAssignmentDto Assignment(Guid id, RotationSource source) =>
        new(id, Guid.NewGuid(), Guid.NewGuid(), DeploymentState.Removed, ActivationState.Inactive,
            null, null, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow, null)
        { Source = source };

    [Fact]
    public async Task CompleteOperation_WhenApiReturnsNonSuccess_Throws()
    {
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.UpdateAssignmentOperation(It.IsAny<Guid>(), It.IsAny<AssignmentOperationStatus>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.BadRequest));

        var sut = new MapRotationActivities(
            Mock.Of<ILogger<MapRotationActivities>>(),
            repositoryApiClientMock.Object,
            Mock.Of<IServersApiClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.CompleteOperation(new CompleteOperationInput(Guid.NewGuid(), AssignmentOperationStatus.Failed, "test")));
    }
}
