using System.Reflection;

using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
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
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();

    public RedirectToGameServerMapSyncTests()
    {
        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    private RedirectToGameServerMapSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _serversApiClientMock.Object,
        _jobTelemetryMock.Object);

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

    [Fact]
    public async Task RunRedirectToGameServerMapSync_WithMixedFtpAndSftpFleet_ProcessesBothServers()
    {
        var ftpServerId = Guid.NewGuid();
        var sftpServerId = Guid.NewGuid();

        var servers = new List<GameServerDto>
        {
            CreateGameServerDto(ftpServerId, "COD4 FTP Server", GameType.CallOfDuty4, FileTransportType.Ftp),
            CreateGameServerDto(sftpServerId, "COD4x SFTP Server", GameType.CallOfDuty4x, FileTransportType.Sftp)
        };

        var response = new ApiResponse<CollectionModel<GameServerDto>>(new CollectionModel<GameServerDto>(servers));
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

        Mock.Get(_serversApiClientMock.Object.Rcon.V1)
            .Setup(x => x.GetServerMaps(It.IsAny<Guid>()))
            .ReturnsAsync(new ApiResult<RconMapCollectionDto>(System.Net.HttpStatusCode.InternalServerError));

        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Setup(x => x.GetLoadedServerMapsFromHost(It.IsAny<Guid>()))
            .ReturnsAsync(new ApiResult<ServerMapsCollectionDto>(System.Net.HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        await sut.RunRedirectToGameServerMapSync(null);

        Mock.Get(_serversApiClientMock.Object.Rcon.V1)
            .Verify(x => x.GetServerMaps(ftpServerId), Times.Once);
        Mock.Get(_serversApiClientMock.Object.Rcon.V1)
            .Verify(x => x.GetServerMaps(sftpServerId), Times.Once);
        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.GetLoadedServerMapsFromHost(ftpServerId), Times.Once);
        Mock.Get(_serversApiClientMock.Object.Maps.V1)
            .Verify(x => x.GetLoadedServerMapsFromHost(sftpServerId), Times.Once);
    }

    private static GameServerDto CreateGameServerDto(Guid serverId, string title, GameType gameType, FileTransportType transportType)
    {
        var dto = new GameServerDto();
        SetDtoProperty(dto, nameof(GameServerDto.GameServerId), serverId);
        SetDtoProperty(dto, nameof(GameServerDto.Title), title);
        SetDtoProperty(dto, nameof(GameServerDto.GameType), gameType);
        SetDtoProperty(dto, nameof(GameServerDto.FileTransportEnabled), true);
        SetDtoProperty(dto, nameof(GameServerDto.FileTransportType), transportType);
        return dto;
    }

    private static void SetDtoProperty(GameServerDto dto, string propertyName, object value)
    {
        var property = typeof(GameServerDto).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on {nameof(GameServerDto)}");

        if (property.SetMethod is not null)
        {
            property.SetValue(dto, value);
            return;
        }

        var field = typeof(GameServerDto).GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Backing field for '{propertyName}' was not found on {nameof(GameServerDto)}");

        field.SetValue(dto, value);
    }
}
