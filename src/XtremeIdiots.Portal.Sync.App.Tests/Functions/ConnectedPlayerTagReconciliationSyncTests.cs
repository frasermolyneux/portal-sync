using Moq;

using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class ConnectedPlayerTagReconciliationSyncTests
{
    private readonly Mock<IRepositoryApiClient> repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IJobTelemetry> jobTelemetryMock = new();

    public ConnectedPlayerTagReconciliationSyncTests()
    {
        jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    [Fact]
    public async Task RunConnectedPlayerTagReconciliationSync_CallsRepositoryReconciliationEndpoint()
    {
        Mock.Get(repositoryApiClientMock.Object.DataMaintenance.V1)
            .Setup(x => x.ReconcileConnectedPlayerTags(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var sut = new ConnectedPlayerTagReconciliationSync(
            repositoryApiClientMock.Object,
            jobTelemetryMock.Object);

        await sut.RunConnectedPlayerTagReconciliationSync(null);

        Mock.Get(repositoryApiClientMock.Object.DataMaintenance.V1)
            .Verify(x => x.ReconcileConnectedPlayerTags(It.IsAny<CancellationToken>()), Times.Once);

        jobTelemetryMock.Verify(x => x.ExecuteAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task>>(),
                It.IsAny<Dictionary<string, string>?>()),
            Times.Once);
    }
}
