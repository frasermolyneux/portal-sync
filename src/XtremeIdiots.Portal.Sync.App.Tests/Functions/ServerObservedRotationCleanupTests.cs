using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class ServerObservedRotationCleanupTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanupUsesOnlyRepositoryObservedCleanupAndSurfacesFailure(bool success)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var telemetry = new Mock<IJobTelemetry>();
        telemetry.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, run, _) => run());
        repository.Setup(x => x.MapRotations.V1.CleanupUnassignedObservedRotations(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(success ? HttpStatusCode.OK : HttpStatusCode.InternalServerError));
        var cleanup = new ServerObservedRotationCleanup(NullLogger<ServerObservedRotationCleanup>.Instance,
            repository.Object, telemetry.Object);

        if (success)
        {
            await cleanup.RunServerObservedRotationCleanupManual(null!);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.RunServerObservedRotationCleanup(null));
        }

        repository.Verify(x => x.MapRotations.V1.CleanupUnassignedObservedRotations(It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.MapRotations.V1.DeleteMapRotation(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.MapRotations.V1.DeleteServerAssignment(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        telemetry.Verify(x => x.ExecuteAsync(nameof(ServerObservedRotationCleanup.RunServerObservedRotationCleanup),
            It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task CancelledCleanupDoesNotStartDeletion()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var telemetry = new Mock<IJobTelemetry>();
        telemetry.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, run, _) => run());
        var cleanup = new ServerObservedRotationCleanup(NullLogger<ServerObservedRotationCleanup>.Instance,
            repository.Object, telemetry.Object);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cleanup.RunServerObservedRotationCleanupManual(null!, cancellation.Token));

        repository.Verify(x => x.MapRotations.V1.CleanupUnassignedObservedRotations(It.IsAny<CancellationToken>()), Times.Never);
    }
}
