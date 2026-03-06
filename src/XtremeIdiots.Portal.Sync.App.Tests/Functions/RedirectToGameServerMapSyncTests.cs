using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class RedirectToGameServerMapSyncTests
{
    private readonly Mock<ILogger<RedirectToGameServerMapSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IServersApiClient> _serversApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration());

    private RedirectToGameServerMapSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _serversApiClientMock.Object,
        _telemetryClient);

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task RunRedirectToGameServerMapSync_WhenApiReturnsFailure_ThrowsApplicationException()
    {
        var failResult = new ApiResult<CollectionModel<GameServerDto>>(System.Net.HttpStatusCode.InternalServerError);

        Mock.Get(_repositoryApiClientMock.Object.GameServers.V1)
            .Setup(x => x.GetGameServers(
                It.IsAny<GameType[]?>(),
                It.IsAny<Guid[]?>(),
                It.IsAny<GameServerFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<GameServerOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        var sut = CreateSut();

        await Assert.ThrowsAsync<ApplicationException>(() => sut.RunRedirectToGameServerMapSync(null));
    }

    [Fact]
    public async Task RunRedirectToGameServerMapSync_WhenApiReturnsEmptyServers_CompletesSuccessfully()
    {
        var emptyCollection = new CollectionModel<GameServerDto>(new List<GameServerDto>());
        var response = new ApiResponse<CollectionModel<GameServerDto>>(emptyCollection);
        var apiResult = new ApiResult<CollectionModel<GameServerDto>>(System.Net.HttpStatusCode.OK, response);

        Mock.Get(_repositoryApiClientMock.Object.GameServers.V1)
            .Setup(x => x.GetGameServers(
                It.IsAny<GameType[]?>(),
                It.IsAny<Guid[]?>(),
                It.IsAny<GameServerFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<GameServerOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var sut = CreateSut();
        await sut.RunRedirectToGameServerMapSync(null);

        Mock.Get(_repositoryApiClientMock.Object.GameServers.V1)
            .Verify(x => x.GetGameServers(It.IsAny<GameType[]?>(), It.IsAny<Guid[]?>(), It.IsAny<GameServerFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<GameServerOrder?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
