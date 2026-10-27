using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationActivitiesTransportTests
{
    [Theory]
    [InlineData(GameType.CallOfDuty4)]
    [InlineData(GameType.CallOfDuty4x)]
    public async Task StockMapSyncRepairAndRemovalNeverUseFileTransport(GameType gameType)
    {
        var serverId = Guid.NewGuid();

        string[] stockMaps = ["mp_bog", "mp_farm", "mp_convoy", "mp_citystreets", "mp_carentan", "mp_crash_snow"];
        foreach (var mapName in stockMaps)
        {
            var synced = await _sut.SyncSingleMapToServer(new(serverId, mapName, gameType));
            var repaired = await _sut.RepairSingleMapToServer(new(serverId, mapName.ToUpperInvariant(), gameType));
            var removed = await _sut.RemoveSingleMapFromServer(new(serverId, mapName, gameType));

            Assert.All([synced, repaired, removed], result =>
            {
                Assert.True(result.Success);
                Assert.Equal(SkipReasons.BuiltInMap, result.SkipReason);
            });
        }
        Mock.Get(_serversApiClientMock.Object.Maps.V1).VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(GameType.CallOfDuty4, GameType.CallOfDuty4x)]
    [InlineData(GameType.CallOfDuty4x, GameType.CallOfDuty4)]
    public async Task GetRotationDetails_UsesServerTypeForCompatibleRotationRouting(GameType rotationType, GameType serverType)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var assignment = new MapRotationServerAssignmentDto(assignmentId, rotationId, serverId,
            DeploymentState.Synced, ActivationState.Inactive, 1, null, "server.cfg", "sv_maprotation",
            null, null, now, now, null);
        var rotation = new MapRotationDto(rotationId, rotationType, "Compatible rotation", null, "war",
            1, "content", now, now, [], [assignment]);
        var server = Newtonsoft.Json.JsonConvert.DeserializeObject<GameServerDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { GameServerId = serverId, GameType = serverType }));
        repository.Setup(x => x.MapRotations.V1.GetServerAssignment(assignmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(System.Net.HttpStatusCode.OK, new ApiResponse<MapRotationServerAssignmentDto>(assignment)));
        repository.Setup(x => x.MapRotations.V1.GetMapRotation(rotationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationDto>(System.Net.HttpStatusCode.OK, new ApiResponse<MapRotationDto>(rotation)));
        repository.Setup(x => x.GameServers.V1.GetGameServer(serverId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<GameServerDto>(System.Net.HttpStatusCode.OK, new ApiResponse<GameServerDto>(server)));
        var activities = new MapRotationActivities(_loggerMock.Object, repository.Object, _serversApiClientMock.Object);

        var details = await activities.GetRotationDetails(new(assignmentId));

        Assert.Equal(serverType, details.GameType);
        Assert.Equal(serverId, details.GameServerId);
        Assert.Equal("war", details.GameMode);
        Assert.Equal(rotationId, details.MapRotationId);
    }

    [Fact]
    public async Task GetRotationDetails_ServerLookupFailureDoesNotFallBackToRotationType()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var assignment = new MapRotationServerAssignmentDto(id, id, id,
            DeploymentState.Synced, ActivationState.Inactive, 1, null, "server.cfg", "sv_maprotation",
            null, null, now, now, null);
        repository.Setup(x => x.MapRotations.V1.GetServerAssignment(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(System.Net.HttpStatusCode.OK, new ApiResponse<MapRotationServerAssignmentDto>(assignment)));
        repository.Setup(x => x.MapRotations.V1.GetMapRotation(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationDto>(System.Net.HttpStatusCode.OK,
                new ApiResponse<MapRotationDto>(new MapRotationDto(id, GameType.CallOfDuty4, "Rotation", null, "war", 1, null, now, now, [], []))));
        repository.Setup(x => x.GameServers.V1.GetGameServer(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<GameServerDto>(System.Net.HttpStatusCode.ServiceUnavailable));
        var activities = new MapRotationActivities(_loggerMock.Object, repository.Object, _serversApiClientMock.Object);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => activities.GetRotationDetails(new(id)));

        Assert.Contains("Failed to get game server", error.Message);
        _serversApiClientMock.VerifyNoOtherCalls();
    }

    private readonly Mock<IServersApiClient> _serversApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<ILogger<MapRotationActivities>> _loggerMock = new();
    private readonly MapRotationActivities _sut;

    public MapRotationActivitiesTransportTests()
    {
        _sut = new MapRotationActivities(
            _loggerMock.Object,
            Mock.Of<IRepositoryApiClient>(),
            _serversApiClientMock.Object);
    }

    [Fact]
    public async Task SyncSingleMapToServer_MixedFleetServerIds_UsesSameTransportNeutralMapPushEndpoint()
    {
        var ftpServerId = Guid.NewGuid();
        var sftpServerId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Setup(x => x.PushServerMapToHost(It.IsAny<Guid>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("test transport-neutral path"));

        var ftpResult = await _sut.SyncSingleMapToServer(new SyncMapInput(ftpServerId, "custom_map_alpha", GameType.CallOfDuty4));
        var sftpResult = await _sut.SyncSingleMapToServer(new SyncMapInput(sftpServerId, "custom_map_alpha", GameType.CallOfDuty4x));

        Assert.False(ftpResult.Success);
        Assert.False(sftpResult.Success);

        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.PushServerMapToHost(ftpServerId, "custom_map_alpha"), Times.Once);
        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.PushServerMapToHost(sftpServerId, "custom_map_alpha"), Times.Once);
    }

    [Fact]
    public async Task RemoveSingleMapFromServer_MixedFleetServerIds_UsesSameTransportNeutralDeleteEndpoint()
    {
        var ftpServerId = Guid.NewGuid();
        var sftpServerId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Setup(x => x.DeleteServerMapFromHost(It.IsAny<Guid>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("test transport-neutral path"));

        var ftpResult = await _sut.RemoveSingleMapFromServer(new RemoveMapInput(ftpServerId, "custom_map_bravo", GameType.CallOfDuty4));
        var sftpResult = await _sut.RemoveSingleMapFromServer(new RemoveMapInput(sftpServerId, "custom_map_bravo", GameType.CallOfDuty4x));

        Assert.False(ftpResult.Success);
        Assert.False(sftpResult.Success);

        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.DeleteServerMapFromHost(ftpServerId, "custom_map_bravo"), Times.Once);
        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.DeleteServerMapFromHost(sftpServerId, "custom_map_bravo"), Times.Once);
    }

    [Fact]
    public async Task SetRconDvar_WhenGameTypeIsCallOfDuty2_UsesCod2SetEndpoint()
    {
        var serverId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.Cod2Rcon.V1)
            .Setup(x => x.Set(
                serverId,
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var result = await _sut.SetRconDvar(new SetRconDvarInput(serverId, GameType.CallOfDuty2, "sv_hostname", "XI Server"));

        Assert.True(result.Success);

        Mock.Get(_serversApiClientMock.Object.Cod2Rcon.V1)
            .Verify(x => x.Set(
                serverId,
                It.Is<SetDvarRequest>(r => r.DvarName == "sv_hostname" && r.Value == "XI Server"),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetRconDvar_WhenGameTypeIsCallOfDuty4x_UsesCoD4xSetEndpoint()
    {
        var serverId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.CoD4xRcon.V1)
            .Setup(x => x.Set(
                serverId,
                It.IsAny<CoD4xSetDvarRequestDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var result = await _sut.SetRconDvar(new SetRconDvarInput(serverId, GameType.CallOfDuty4x, "sv_hostname", "XI Server"));

        Assert.True(result.Success);

        Mock.Get(_serversApiClientMock.Object.CoD4xRcon.V1)
            .Verify(x => x.Set(
                serverId,
                It.Is<CoD4xSetDvarRequestDto>(r => r.DvarName == "sv_hostname" && r.Value == "XI Server"),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetRconDvar_WhenGameTypeIsCallOfDuty4_UsesCod4SetEndpoint()
    {
        var serverId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.Cod4Rcon.V1)
            .Setup(x => x.Set(
                serverId,
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var result = await _sut.SetRconDvar(new SetRconDvarInput(serverId, GameType.CallOfDuty4, "sv_hostname", "XI Server"));

        Assert.True(result.Success);

        Mock.Get(_serversApiClientMock.Object.Cod4Rcon.V1)
            .Verify(x => x.Set(
                serverId,
                It.Is<SetDvarRequest>(r => r.DvarName == "sv_hostname" && r.Value == "XI Server"),
                It.IsAny<CancellationToken>()), Times.Once);

        Mock.Get(_serversApiClientMock.Object.CoD4xRcon.V1)
            .Verify(x => x.Set(
                It.IsAny<Guid>(),
                It.IsAny<CoD4xSetDvarRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetRconDvar_WhenGameTypeIsCallOfDuty5_UsesCod5SetEndpoint()
    {
        var serverId = Guid.NewGuid();

        Mock.Get(_serversApiClientMock.Object.Cod5Rcon.V1)
            .Setup(x => x.Set(
                serverId,
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<string>(
                System.Net.HttpStatusCode.OK,
                new ApiResponse<string>("ok")));

        var result = await _sut.SetRconDvar(new SetRconDvarInput(serverId, GameType.CallOfDuty5, "sv_hostname", "XI Server"));

        Assert.True(result.Success);

        Mock.Get(_serversApiClientMock.Object.Cod5Rcon.V1)
            .Verify(x => x.Set(
                serverId,
                It.Is<SetDvarRequest>(r => r.DvarName == "sv_hostname" && r.Value == "XI Server"),
                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetRconDvar_WhenGameTypeIsUnsupported_ReturnsFailureWithoutCallingAnySetEndpoint()
    {
        var serverId = Guid.NewGuid();
        _ = _loggerMock.Setup(x => x.IsEnabled(LogLevel.Warning)).Returns(true);
        const string dvarName = "sv_hostname";

        var result = await _sut.SetRconDvar(new SetRconDvarInput(serverId, GameType.Insurgency, dvarName, "XI Server"));
        var error = result.Error ?? string.Empty;

        Assert.False(result.Success);
        Assert.Equal(dvarName, result.MapName);
        Assert.Contains(GameType.Insurgency.ToString(), error);
        Assert.Contains("not supported", error, StringComparison.OrdinalIgnoreCase);

        _loggerMock.Verify(x => x.Log(
            LogLevel.Warning,
            It.Is<EventId>(eventId => eventId.Id == 0),
            It.Is<It.IsAnyType>((state, _) =>
                state != null
                && state.ToString()!.Contains(dvarName, StringComparison.Ordinal)
                && state.ToString()!.Contains(serverId.ToString(), StringComparison.Ordinal)
                && state.ToString()!.Contains(error, StringComparison.Ordinal)),
            It.IsAny<Exception?>(),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);

        Mock.Get(_serversApiClientMock.Object.Cod2Rcon.V1)
            .Verify(x => x.Set(
                It.IsAny<Guid>(),
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()), Times.Never);

        Mock.Get(_serversApiClientMock.Object.Cod4Rcon.V1)
            .Verify(x => x.Set(
                It.IsAny<Guid>(),
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()), Times.Never);

        Mock.Get(_serversApiClientMock.Object.Cod5Rcon.V1)
            .Verify(x => x.Set(
                It.IsAny<Guid>(),
                It.IsAny<SetDvarRequest>(),
                It.IsAny<CancellationToken>()), Times.Never);

        Mock.Get(_serversApiClientMock.Object.CoD4xRcon.V1)
            .Verify(x => x.Set(
                It.IsAny<Guid>(),
                It.IsAny<CoD4xSetDvarRequestDto>(),
                It.IsAny<CancellationToken>()), Times.Never);
    }
}
