using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Models;
using XtremeIdiots.Portal.Sync.App.Redirect;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class MapRedirectSyncTests
{
    private readonly Mock<ILogger<MapRedirectSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IMapRedirectRepository> _mapRedirectRepositoryMock = new();
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration());
    private readonly IConfiguration _configuration;

    public MapRedirectSyncTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MapRedirect:BaseUrl"] = "https://redirect.test.com"
            })
            .Build();
    }

    private MapRedirectSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _mapRedirectRepositoryMock.Object,
        _configuration,
        _telemetryClient);

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenGetMapEntriesReturnsEmpty_CompletesWithoutError()
    {
        _mapRedirectRepositoryMock
            .Setup(x => x.GetMapEntriesForGame(It.IsAny<string>()))
            .ReturnsAsync(new List<MapRedirectEntry>());

        var emptyCollection = new CollectionModel<MapDto>(new List<MapDto>());
        var response = new ApiResponse<CollectionModel<MapDto>>(emptyCollection);
        var apiResult = new ApiResult<CollectionModel<MapDto>>(System.Net.HttpStatusCode.OK, response);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(x => x.GetMaps(
                It.IsAny<GameType>(),
                It.IsAny<string[]?>(),
                It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<MapsOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        _mapRedirectRepositoryMock.Verify(
            x => x.GetMapEntriesForGame(It.IsAny<string>()),
            Times.AtLeastOnce);
    }
}
