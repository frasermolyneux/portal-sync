using Microsoft.Extensions.Logging;
using Moq;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationActivitiesTransportTests
{
    private readonly Mock<IServersApiClient> _serversApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly MapRotationActivities _sut;

    public MapRotationActivitiesTransportTests()
    {
        _sut = new MapRotationActivities(
            Mock.Of<ILogger<MapRotationActivities>>(),
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
}
