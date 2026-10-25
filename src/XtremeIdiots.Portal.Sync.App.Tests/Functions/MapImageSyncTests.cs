using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using Newtonsoft.Json;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Configuration;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class MapImageSyncTests
{
    private readonly Mock<ILogger<MapImageSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly HttpClient _httpClient = new();
    private readonly IOptions<MapImagesStorageOptions> _mapImagesStorageOptions = Options.Create(new MapImagesStorageOptions
    {
        StorageBlobEndpoint = "https://test.blob.core.windows.net",
        ContainerName = "map-images"
    });
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> _auditLoggerMock = new();
    private readonly IConfiguration _configuration;

    public MapImageSyncTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GameTracker:MapImageBaseUrl"] = "https://image.gametracker.com/images/maps/160x120/"
            })
            .Build();

        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    private MapImageSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _httpClient,
        _mapImagesStorageOptions,
        _jobTelemetryMock.Object,
        _configuration,
        _auditLoggerMock.Object);

    [Fact]
    public async Task SuccessfulUploadsDoNotSkipLaterPagesOfTheShrinkingMissingImageInventory()
    {
        var maps = Enumerable.Range(0, 51).Select(index => JsonConvert.DeserializeObject<MapDto>(
            JsonConvert.SerializeObject(new { MapId = Guid.NewGuid(), MapName = $"map-{index}", GameType = GameType.CallOfDuty2 }))
            ?? throw new InvalidOperationException("Invalid map fixture.")).ToList();
        Mock.Get(_repositoryApiClientMock.Object.DataMaintenance.V1)
            .Setup(client => client.ValidateMapImages(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(client => client.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameType? game, string[]? _, MapsFilter? _, string? _, int skip, int take,
                MapsOrder? _, CancellationToken _) => new ApiResult<CollectionModel<MapDto>>(HttpStatusCode.OK,
                    new ApiResponse<CollectionModel<MapDto>>(new CollectionModel<MapDto>(
                        game == GameType.CallOfDuty2 ? maps.Skip(skip).Take(take).ToArray() : []))));
        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(client => client.UpdateMapImage(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((id, _, _) => maps.RemoveAll(map => map.MapId == id))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        using var http = new HttpClient(new ImageHandler());
        var sut = new MapImageSync(_loggerMock.Object, _repositoryApiClientMock.Object, http,
            _mapImagesStorageOptions, _jobTelemetryMock.Object, _configuration, _auditLoggerMock.Object);

        await sut.RunMapImageSync(null);

        Assert.Empty(maps);
        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Verify(client => client.UpdateMapImage(It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Exactly(51));
    }

    private sealed class ImageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MapImageSync(
            null!,
            _repositoryApiClientMock.Object,
            _httpClient,
            _mapImagesStorageOptions,
            _jobTelemetryMock.Object,
            _configuration,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullRepositoryApiClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MapImageSync(
            _loggerMock.Object,
            null!,
            _httpClient,
            _mapImagesStorageOptions,
            _jobTelemetryMock.Object,
            _configuration,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullHttpClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MapImageSync(
            _loggerMock.Object,
            _repositoryApiClientMock.Object,
            null!,
            _mapImagesStorageOptions,
            _jobTelemetryMock.Object,
            _configuration,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullMapImagesStorageOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MapImageSync(
            _loggerMock.Object,
            _repositoryApiClientMock.Object,
            _httpClient,
            null!,
            _jobTelemetryMock.Object,
            _configuration,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullTelemetryClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MapImageSync(
            _loggerMock.Object,
            _repositoryApiClientMock.Object,
            _httpClient,
            _mapImagesStorageOptions,
            null!,
            _configuration,
            _auditLoggerMock.Object));
    }

    [Fact]
    public async Task RunMapImageSync_WhenMapsReturnEmpty_CompletesSuccessfully()
    {
        Mock.Get(_repositoryApiClientMock.Object.DataMaintenance.V1)
            .Setup(x => x.ValidateMapImages(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var emptyCollection = new CollectionModel<MapDto>(new List<MapDto>());
        var response = new ApiResponse<CollectionModel<MapDto>>(emptyCollection);
        var apiResult = new ApiResult<CollectionModel<MapDto>>(HttpStatusCode.OK, response);

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
        await sut.RunMapImageSync(null);

        Mock.Get(_repositoryApiClientMock.Object.DataMaintenance.V1)
            .Verify(x => x.ValidateMapImages(It.IsAny<CancellationToken>()), Times.Once);
    }
}
