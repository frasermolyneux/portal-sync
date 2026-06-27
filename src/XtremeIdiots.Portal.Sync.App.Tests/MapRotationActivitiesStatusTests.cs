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
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.BadRequest));

        var sut = new MapRotationActivities(
            Mock.Of<ILogger<MapRotationActivities>>(),
            repositoryApiClientMock.Object,
            Mock.Of<IServersApiClient>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateAssignmentStatus(new UpdateStatusInput(Guid.NewGuid(), DeploymentState: DeploymentState.Removed)));
    }

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
