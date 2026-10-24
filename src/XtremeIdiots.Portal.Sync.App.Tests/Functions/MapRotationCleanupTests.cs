using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class MapRotationCleanupTests
{
    [Theory]
    [InlineData(DeploymentState.Removing, 100, false)]
    [InlineData(DeploymentState.Removed, 3, false)]
    [InlineData(DeploymentState.Removed, 89, false)]
    [InlineData(DeploymentState.Removed, 91, true)]
    public async Task RetainsUncertainRemovalsAndNinetyDaysOfEvidence(DeploymentState state, int age, bool deletes)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var jobs = new Mock<IJobTelemetry>();
        jobs.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, run, _) => run());
        var audit = new Mock<IAuditLogger>();
        var id = Guid.NewGuid();
        var timestamp = DateTime.UtcNow.AddDays(-age);
        var items = new CollectionModel<MapRotationServerAssignmentDto>(
            [new(id, Guid.NewGuid(), Guid.NewGuid(), state, ActivationState.Inactive,
                null, null, "server.cfg", "sv_maprotation", null, null, timestamp, timestamp, timestamp)]);
        var api = Mock.Get(repository.Object.MapRotations.V1);
        api.Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(items)));
        api.Setup(x => x.DeleteServerAssignment(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var cleanup = new MapRotationCleanup(Mock.Of<ILogger<MapRotationCleanup>>(), repository.Object, jobs.Object, audit.Object);
        await cleanup.RunMapRotationCleanup(null);

        api.Verify(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()), Times.Never);
        api.Verify(x => x.DeleteServerAssignment(id, It.IsAny<CancellationToken>()), deletes ? Times.Once : Times.Never);
        audit.Verify(x => x.LogAudit(It.IsAny<AuditEvent>()), deletes ? Times.Once : Times.Never);
        jobs.Verify(x => x.ExecuteAsync(nameof(MapRotationCleanup.RunMapRotationCleanup),
            It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }
}
