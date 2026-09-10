using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
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
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> _auditLoggerMock = new();
    private readonly IConfiguration _configuration;

    public MapRedirectSyncTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MapRedirect:BaseUrl"] = "https://redirect.test.com"
            })
            .Build();

        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    private MapRedirectSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _mapRedirectRepositoryMock.Object,
        _configuration,
        _jobTelemetryMock.Object,
        _auditLoggerMock.Object);

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

    [Fact]
    public async Task RunMapRedirectSync_WhenRedirectEntryHasNoPlayableFiles_DoesNotCreateMap()
    {
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_empty", MapFiles = [] });
        SetupRepositoryMaps();

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1).Verify(
            x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenRedirectEntryHasOnlyNonPlayableFiles_DoesNotCreateMap()
    {
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_readme", MapFiles = ["readme.txt"] });
        SetupRepositoryMaps();

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1).Verify(
            x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenRedirectEntryHasPlayableFiles_CreatesMap()
    {
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_custom", MapFiles = ["mp_custom.iwd", "readme.txt"] });
        SetupRepositoryMaps();

        List<CreateMapDto>? created = null;
        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()))
            .Callback<List<CreateMapDto>, CancellationToken>((maps, _) => created = maps)
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        var createdMap = Assert.Single(created!, m => m.MapName == "mp_custom");
        var mapFile = Assert.Single(createdMap.MapFiles);
        Assert.Equal("mp_custom.iwd", mapFile.FileName);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenExistingMapFilesChangedWithSameCount_UpdatesMap()
    {
        var mapId = Guid.NewGuid();
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_custom", MapFiles = ["mp_custom_v2.iwd"] });
        SetupRepositoryMaps(MapDtoFactory.Create(
            mapId, GameType.CallOfDuty4, "mp_custom",
            [new MapFileDto("mp_custom_v1.iwd", "https://redirect.test.com/old")]));

        List<EditMapDto>? updated = null;
        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()))
            .Callback<List<EditMapDto>, CancellationToken>((maps, _) => updated = maps)
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        var updatedMap = Assert.Single(updated!);
        Assert.Equal(mapId, updatedMap.MapId);
        Assert.Equal("mp_custom_v2.iwd", Assert.Single(updatedMap.MapFiles).FileName);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenExistingMapFilesUnchanged_DoesNotUpdateMap()
    {
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_custom", MapFiles = ["mp_custom.iwd"] });
        SetupRepositoryMaps(MapDtoFactory.Create(
            Guid.NewGuid(), GameType.CallOfDuty4, "mp_custom",
            [new MapFileDto("mp_custom.iwd", "https://redirect.test.com/current")]));

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1).Verify(
            x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunMapRedirectSync_WhenExistingMapHasNoPlayableFilesOnRedirect_DoesNotWipeMapFiles()
    {
        SetupRedirectEntries(new MapRedirectEntry { MapName = "mp_custom", MapFiles = [] });
        SetupRepositoryMaps(MapDtoFactory.Create(
            Guid.NewGuid(), GameType.CallOfDuty4, "mp_custom",
            [new MapFileDto("mp_custom.iwd", "https://redirect.test.com/current")]));

        var sut = CreateSut();
        await sut.RunMapRedirectSync(null);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1).Verify(
            x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupRedirectEntries(params MapRedirectEntry[] entries)
        => _mapRedirectRepositoryMock
            .Setup(x => x.GetMapEntriesForGame(It.IsAny<string>()))
            .ReturnsAsync(entries.ToList());

    private void SetupRepositoryMaps(params MapDto[] maps)
    {
        var mapsApiMock = Mock.Get(_repositoryApiClientMock.Object.Maps.V1);

        // The sync pages until an empty batch is returned; the first page carries all maps for the game
        mapsApiMock
            .Setup(x => x.GetMaps(
                It.IsAny<GameType>(),
                It.IsAny<string[]?>(),
                It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(),
                It.Is<int>(skip => skip > 0),
                It.IsAny<int>(),
                It.IsAny<MapsOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapDto>>(new CollectionModel<MapDto>(new List<MapDto>()))));

        mapsApiMock
            .Setup(x => x.GetMaps(
                It.IsAny<GameType>(),
                It.IsAny<string[]?>(),
                It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(),
                0,
                It.IsAny<int>(),
                It.IsAny<MapsOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapDto>>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapDto>>(new CollectionModel<MapDto>(maps.ToList()))));
    }
}
