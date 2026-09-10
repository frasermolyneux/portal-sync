using System.Net;

using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class MapRotationActivitiesTests
{
    private readonly Mock<ILogger<MapRotationActivities>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IServersApiClient> _serversApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };

    private MapRotationActivities CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _serversApiClientMock.Object);

    private Mock<Integrations.Servers.Abstractions.Interfaces.V1.IMapsApi> ServerMapsApi
        => Mock.Get(_serversApiClientMock.Object.Maps.V1);

    private void SetupPush(HttpStatusCode statusCode)
        => ServerMapsApi
            .Setup(x => x.PushServerMapToHost(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new ApiResult(statusCode));

    private void SetupDelete(HttpStatusCode statusCode)
        => ServerMapsApi
            .Setup(x => x.DeleteServerMapFromHost(It.IsAny<Guid>(), It.IsAny<string>()))
            .ReturnsAsync(new ApiResult(statusCode));

    private void SetupMap(Guid mapId, string mapName, List<MapFileDto>? mapFiles)
    {
        var map = MapDtoFactory.Create(mapId, GameType.CallOfDuty4, mapName, mapFiles);

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(x => x.GetMap(mapId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.OK, new ApiResponse<MapDto>(map)));
    }

    [Fact]
    public async Task SyncSingleMapToServer_WhenPushSucceeds_DoesNotRemoveMapFromHost()
    {
        SetupPush(HttpStatusCode.OK);
        SetupDelete(HttpStatusCode.OK);

        var result = await CreateSut().SyncSingleMapToServer(
            new SyncMapInput(Guid.NewGuid(), "mp_custom", GameType.CallOfDuty4));

        Assert.True(result.Success);
        Assert.Null(result.SkipReason);
        ServerMapsApi.Verify(x => x.DeleteServerMapFromHost(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SyncSingleMapToServer_WhenPushReportsNoMapFiles_RemovesIncompleteMapDirectory()
    {
        var gameServerId = Guid.NewGuid();
        SetupPush(HttpStatusCode.BadRequest);
        SetupDelete(HttpStatusCode.OK);

        var result = await CreateSut().SyncSingleMapToServer(
            new SyncMapInput(gameServerId, "mp_empty", GameType.CallOfDuty4));

        Assert.True(result.Success);
        Assert.Equal(SkipReasons.NoMapFiles, result.SkipReason);
        ServerMapsApi.Verify(x => x.DeleteServerMapFromHost(gameServerId, "mp_empty"), Times.Once);
    }

    [Fact]
    public async Task SyncSingleMapToServer_WhenCleanUpFails_StillReportsSkip()
    {
        SetupPush(HttpStatusCode.BadRequest);
        SetupDelete(HttpStatusCode.InternalServerError);

        var result = await CreateSut().SyncSingleMapToServer(
            new SyncMapInput(Guid.NewGuid(), "mp_empty", GameType.CallOfDuty4));

        Assert.True(result.Success);
        Assert.Equal(SkipReasons.NoMapFiles, result.SkipReason);
    }

    [Fact]
    public async Task SyncSingleMapToServer_WhenForced_RemovesMapBeforePushing()
    {
        var gameServerId = Guid.NewGuid();
        var callOrder = new List<string>();

        ServerMapsApi
            .Setup(x => x.DeleteServerMapFromHost(gameServerId, "mp_custom"))
            .Callback(() => callOrder.Add("delete"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        ServerMapsApi
            .Setup(x => x.PushServerMapToHost(gameServerId, "mp_custom"))
            .Callback(() => callOrder.Add("push"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        var result = await CreateSut().SyncSingleMapToServer(
            new SyncMapInput(gameServerId, "mp_custom", GameType.CallOfDuty4, Force: true));

        Assert.True(result.Success);
        Assert.Equal(["delete", "push"], callOrder);
    }

    [Fact]
    public async Task SyncSingleMapToServer_WhenMapIsBuiltIn_DoesNotCallHost()
    {
        var result = await CreateSut().SyncSingleMapToServer(
            new SyncMapInput(Guid.NewGuid(), "mp_crash", GameType.CallOfDuty4, Force: true));

        Assert.True(result.Success);
        Assert.Equal(SkipReasons.BuiltInMap, result.SkipReason);
        ServerMapsApi.Verify(x => x.PushServerMapToHost(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        ServerMapsApi.Verify(x => x.DeleteServerMapFromHost(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ResolveRotationMaps_ReportsWhetherEachMapHasFiles()
    {
        var withFilesId = Guid.NewGuid();
        var withoutFilesId = Guid.NewGuid();
        var nullFilesId = Guid.NewGuid();

        SetupMap(withFilesId, "mp_with_files", [new MapFileDto("mp_with_files.iwd", "https://redirect.test/mp_with_files.iwd")]);
        SetupMap(withoutFilesId, "mp_without_files", []);
        SetupMap(nullFilesId, "mp_null_files", null);

        var result = await CreateSut().ResolveRotationMaps(
            new ResolveRotationMapsInput([withFilesId, withoutFilesId, nullFilesId]));

        Assert.Equal(
        [
            new RotationMapDetail("mp_with_files", true),
            new RotationMapDetail("mp_without_files", false),
            new RotationMapDetail("mp_null_files", false)
        ], result);
    }

    [Fact]
    public async Task ResolveRotationMaps_FetchesEachMapOnlyOnce()
    {
        var mapId = Guid.NewGuid();
        SetupMap(mapId, "mp_custom", [new MapFileDto("mp_custom.iwd", "https://redirect.test/mp_custom.iwd")]);

        await CreateSut().ResolveRotationMaps(new ResolveRotationMapsInput([mapId]));

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Verify(x => x.GetMap(mapId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveRotationMaps_WhenMapCannotBeResolved_Throws()
    {
        var mapId = Guid.NewGuid();

        Mock.Get(_repositoryApiClientMock.Object.Maps.V1)
            .Setup(x => x.GetMap(mapId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapDto>(HttpStatusCode.NotFound, new ApiResponse<MapDto>()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSut().ResolveRotationMaps(new ResolveRotationMapsInput([mapId])));
    }
}
