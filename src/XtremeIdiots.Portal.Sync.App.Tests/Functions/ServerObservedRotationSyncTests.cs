using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.GameServers;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class ServerObservedRotationSyncTests
{
    private readonly Mock<IRepositoryApiClient> repository = new() { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IServersApiClient> servers = new() { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IJobTelemetry> telemetry = new();

    public ServerObservedRotationSyncTests()
    {
        telemetry.Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, run, _) => run());
    }

    [Theory]
    [InlineData(GameType.CallOfDuty2)]
    [InlineData(GameType.CallOfDuty4)]
    [InlineData(GameType.CallOfDuty4x)]
    [InlineData(GameType.CallOfDuty5)]
    public async Task CollectsOrderedRepeatedMapsAndUnspecifiedModeWithoutServerWrites(GameType game)
    {
        var server = Server(game);
        Inventory([server]);
        Unmanaged(server.GameServerId);
        Rcon(server, Success(new RconMapCollectionDto(
            [new("", "MP_First"), new("War", "mp_Second"), new("SD", "MP_First")])));
        ObserveServerRotationDto? observation = null;
        repository.Setup(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
                It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ObserveServerRotationDto, CancellationToken>((_, value, _) => observation = value)
            .ReturnsAsync(Success(Assignment(server.GameServerId)));
        var before = DateTimeOffset.UtcNow;

        await Sut().RunServerObservedRotationSyncManual(null!);

        Assert.NotNull(observation);
        Assert.InRange(observation.ObservedAt, before, DateTimeOffset.UtcNow);
        Assert.Equal(["MP_First", "mp_Second", "MP_First"], observation.Entries.Select(entry => entry.MapName));
        Assert.Equal([null, "War", "SD"], observation.Entries.Select(entry => entry.GameMode));
        telemetry.Verify(x => x.ExecuteAsync(nameof(ServerObservedRotationSync.RunServerObservedRotationSync),
            It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()), Times.Once);
        Mock.Get(servers.Object.Maps.V1).VerifyNoOtherCalls();
        Mock.Get(servers.Object.Config.V1).VerifyNoOtherCalls();
        Mock.Get(repository.Object.Maps.V1).VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExplicitEmptyIsPersistedRatherThanConfusedWithMissingData()
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory([server]);
        Unmanaged(server.GameServerId);
        Rcon(server, Success(new RconMapCollectionDto([])));
        repository.Setup(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
                It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(Assignment(server.GameServerId)));

        await Sut().RunServerObservedRotationSync(null);

        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
            It.Is<ObserveServerRotationDto>(dto => dto.Entries.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RconErrorEnvelopeWithHttpSuccessIsNotAnEmptyObservation()
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory([server]);
        Unmanaged(server.GameServerId);
        Rcon(server, new ApiResult<RconMapCollectionDto>(HttpStatusCode.OK,
            new ApiResponse<RconMapCollectionDto>([new ApiError("RCON_OPERATION_FAILED", "Malformed rotation")])));

        await Assert.ThrowsAsync<PartialJobFailureException>(() => Sut().RunServerObservedRotationSync(null));

        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(It.IsAny<Guid>(),
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task MalformedMapNameDoesNotReplacePreviousObservation(string mapName)
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory([server]);
        Unmanaged(server.GameServerId);
        Rcon(server, Success(new RconMapCollectionDto([new("war", mapName)])));

        await Assert.ThrowsAsync<PartialJobFailureException>(() => Sut().RunServerObservedRotationSync(null));

        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(It.IsAny<Guid>(),
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InventoryPaginationUsesRawPageSizeRatherThanEligibleServerCount()
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory(Enumerable.Range(0, 100).Select(_ => Server(GameType.CallOfDuty4, rconEnabled: false)).ToList());
        repository.Setup(x => x.GameServers.V1.GetGameServers(It.IsAny<GameType[]?>(), null, null,
                100, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<GameServerDto>([server])));
        Unmanaged(server.GameServerId);
        Rcon(server, Success(new RconMapCollectionDto([])));
        repository.Setup(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
                It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(Assignment(server.GameServerId)));

        await Sut().RunServerObservedRotationSync(null);

        repository.Verify(x => x.GameServers.V1.GetGameServers(It.IsAny<GameType[]?>(), null, null,
            100, 100, null, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrMissingReadsRetainSnapshotAndContinueOtherServers(bool failedResponse)
    {
        var first = Server(GameType.CallOfDuty4);
        var second = Server(GameType.CallOfDuty4);
        Inventory([first, second]);
        Unmanaged(first.GameServerId);
        Unmanaged(second.GameServerId);
        Rcon(first, failedResponse
            ? new ApiResult<RconMapCollectionDto>(HttpStatusCode.ServiceUnavailable)
            : Success(new RconMapCollectionDto(null!)));
        Rcon(second, Success(new RconMapCollectionDto([new("war", "mp_crash")])));
        repository.Setup(x => x.MapRotations.V1.ReplaceObservedServerRotation(second.GameServerId,
                It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(Assignment(second.GameServerId)));

        var failure = await Assert.ThrowsAsync<PartialJobFailureException>(() => Sut().RunServerObservedRotationSync(null));

        Assert.Single(failure.InnerExceptions);
        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(first.GameServerId,
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(second.GameServerId,
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManagedOrIneligibleServersAreNotRead()
    {
        var managed = Server(GameType.CallOfDuty4);
        Inventory([managed, Server(GameType.CallOfDuty4, rconEnabled: false),
            Server(GameType.CallOfDuty5, deleted: true), Server(GameType.Unknown)]);
        repository.Setup(x => x.MapRotations.V1.GetServerAssignments(null, managed.GameServerId, null,
                0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<MapRotationServerAssignmentDto>([Assignment(managed.GameServerId, RotationSource.PortalManaged)])));

        await Sut().RunServerObservedRotationSync(null);

        Mock.Get(servers.Object.Cod4Rcon.V1).VerifyNoOtherCalls();
        Mock.Get(servers.Object.Cod5Rcon.V1).VerifyNoOtherCalls();
        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(It.IsAny<Guid>(),
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManagedAssignmentOnLaterPagePreventsCollection()
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory([server]);
        var removed = Enumerable.Range(0, 100).Select(_ =>
            Assignment(server.GameServerId, RotationSource.PortalManaged) with { DeploymentState = DeploymentState.Removed }).ToList();
        repository.Setup(x => x.MapRotations.V1.GetServerAssignments(null, server.GameServerId, null,
                0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<MapRotationServerAssignmentDto>(removed)));
        repository.Setup(x => x.MapRotations.V1.GetServerAssignments(null, server.GameServerId, null,
                100, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<MapRotationServerAssignmentDto>(
                [Assignment(server.GameServerId, RotationSource.PortalManaged)])));

        await Sut().RunServerObservedRotationSync(null);

        Mock.Get(servers.Object.Cod4Rcon.V1).VerifyNoOtherCalls();
        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(It.IsAny<Guid>(),
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationDuringRconStopsBeforePersistenceAndOtherServers(bool rconThrows)
    {
        var first = Server(GameType.CallOfDuty4);
        var second = Server(GameType.CallOfDuty4);
        Inventory([first, second]);
        Unmanaged(first.GameServerId);
        using var cancellation = new CancellationTokenSource();
        servers.Setup(x => x.Cod4Rcon.V1.GetMaps(first.GameServerId, cancellation.Token))
            .Returns((Guid _, CancellationToken token) =>
            {
                cancellation.Cancel();
                return rconThrows
                    ? Task.FromCanceled<ApiResult<RconMapCollectionDto>>(token)
                    : Task.FromResult(Success(new RconMapCollectionDto([new("war", "mp_crash")])));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Sut().RunServerObservedRotationSyncManual(null!, cancellation.Token));

        repository.Verify(x => x.MapRotations.V1.ReplaceObservedServerRotation(It.IsAny<Guid>(),
            It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()), Times.Never);
        repository.Verify(x => x.MapRotations.V1.GetServerAssignments(null, second.GameServerId, null,
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        servers.Verify(x => x.Cod4Rcon.V1.GetMaps(first.GameServerId, cancellation.Token), Times.Once);
    }

    [Theory]
    [InlineData("PORTAL_MANAGED_ASSIGNMENT_EXISTS", false)]
    [InlineData("OBSERVATION_SUPERSEDED", false)]
    [InlineData("OBSERVATION_CONFLICT", true)]
    [InlineData("UNKNOWN_CONFLICT", true)]
    public async Task OnlyKnownOwnershipAndNewerPollConflictsAreSafeSkips(string code, bool fails)
    {
        var server = Server(GameType.CallOfDuty4);
        Inventory([server]);
        Unmanaged(server.GameServerId);
        Rcon(server, Success(new RconMapCollectionDto([new("war", "mp_crash")])));
        repository.Setup(x => x.MapRotations.V1.ReplaceObservedServerRotation(server.GameServerId,
                It.IsAny<ObserveServerRotationDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(HttpStatusCode.Conflict,
                new ApiResponse<MapRotationServerAssignmentDto>([new ApiError(code, "Conflict")])));

        if (fails)
        {
            await Assert.ThrowsAsync<PartialJobFailureException>(() => Sut().RunServerObservedRotationSync(null));
        }
        else
        {
            await Sut().RunServerObservedRotationSync(null);
        }
        Mock.Get(servers.Object.Config.V1).VerifyNoOtherCalls();
        Mock.Get(servers.Object.Maps.V1).VerifyNoOtherCalls();
    }

    private ServerObservedRotationSync Sut() => new(NullLogger<ServerObservedRotationSync>.Instance,
        repository.Object, servers.Object, telemetry.Object);

    private void Inventory(List<GameServerDto> items) =>
        repository.Setup(x => x.GameServers.V1.GetGameServers(It.IsAny<GameType[]?>(), null, null,
                0, 100, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<GameServerDto>(items)));

    private void Unmanaged(Guid serverId) =>
        repository.Setup(x => x.MapRotations.V1.GetServerAssignments(null, serverId, null,
                0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Success(new CollectionModel<MapRotationServerAssignmentDto>([])));

    private void Rcon(GameServerDto server, ApiResult<RconMapCollectionDto> response)
    {
        switch (server.GameType)
        {
            case GameType.CallOfDuty2:
                servers.Setup(x => x.Cod2Rcon.V1.GetMaps(server.GameServerId, It.IsAny<CancellationToken>())).ReturnsAsync(response);
                break;
            case GameType.CallOfDuty4:
                servers.Setup(x => x.Cod4Rcon.V1.GetMaps(server.GameServerId, It.IsAny<CancellationToken>())).ReturnsAsync(response);
                break;
            case GameType.CallOfDuty4x:
                servers.Setup(x => x.CoD4xRcon.V1.GetMaps(server.GameServerId, It.IsAny<CancellationToken>())).ReturnsAsync(response);
                break;
            case GameType.CallOfDuty5:
                servers.Setup(x => x.Cod5Rcon.V1.GetMaps(server.GameServerId, It.IsAny<CancellationToken>())).ReturnsAsync(response);
                break;
        }
    }

    private static GameServerDto Server(GameType game, bool rconEnabled = true, bool deleted = false) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<GameServerDto>(Newtonsoft.Json.JsonConvert.SerializeObject(
            new { GameServerId = Guid.NewGuid(), GameType = game, RconEnabled = rconEnabled, Deleted = deleted }))!;

    private static MapRotationServerAssignmentDto Assignment(Guid serverId, RotationSource source = RotationSource.ServerObserved) =>
        new(Guid.NewGuid(), Guid.NewGuid(), serverId, DeploymentState.Synced, ActivationState.Inactive,
            1, null, "server.cfg", "sv_maprotation", null, null, DateTime.UtcNow, DateTime.UtcNow, null)
        { Source = source };

    private static ApiResult<T> Success<T>(T data) => new(HttpStatusCode.OK, new ApiResponse<T>(data));
}
