using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using Newtonsoft.Json;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Models;
using XtremeIdiots.Portal.Sync.App.Redirect;
using XtremeIdiots.Redirect.Api.Abstractions.V1;
using XtremeIdiots.Redirect.Api.Client.Testing;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class MapRedirectSyncTests
{
    private const string DirectOrigin = "https://redirect.xtremeidiots.net";
    private readonly Mock<ILogger<MapRedirectSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IMapRedirectRepository> _mapRedirectRepositoryMock = new();
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> _auditLoggerMock = new();
    private Mock<IMapsApi> Maps => Mock.Get(_repositoryApiClientMock.Object.Maps.V1);

    public MapRedirectSyncTests()
    {
        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
        _mapRedirectRepositoryMock
            .Setup(x => x.GetSnapshotForGame(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MapRedirectSnapshot(DirectOrigin, []));
        ConfigureRepository();
        Maps.Setup(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        Maps.Setup(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
    }

    private MapRedirectSync CreateSut() => new(_loggerMock.Object, _repositoryApiClientMock.Object,
        _mapRedirectRepositoryMock.Object, _jobTelemetryMock.Object, _auditLoggerMock.Object);

    private void ConfigureSnapshot(params ContentEntry[] files) =>
        _mapRedirectRepositoryMock
            .Setup(x => x.GetSnapshotForGame("cod4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MapRedirectSnapshot(DirectOrigin, [RedirectDtoFactory.CreateManifest("cod4", "mp_test", files)]));

    private void ConfigureRepository(params MapFileDto[] files)
    {
        Maps.Setup(x => x.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(), It.IsAny<CancellationToken>()))
            .Returns((GameType? game, string[]? names, MapsFilter? filter, string? search, int skip, int take,
                MapsOrder? order, CancellationToken cancellationToken) => Task.FromResult(Collection(
                    skip == 0 && files.Length > 0 && game is GameType.CallOfDuty4 or GameType.CallOfDuty4x
                        ? [StoredMap(game.Value, files)] : [])));
    }

    private static MapDto StoredMap(GameType game, MapFileDto[] files) =>
        JsonConvert.DeserializeObject<MapDto>(JsonConvert.SerializeObject(new
        {
            MapId = Guid.NewGuid(),
            GameType = game,
            MapName = "mp_test",
            MapFiles = files
        })) ?? throw new InvalidOperationException("Invalid map fixture.");

    private static ApiResult<CollectionModel<MapDto>> Collection(List<MapDto> maps) =>
        new(HttpStatusCode.OK, new ApiResponse<CollectionModel<MapDto>>(new CollectionModel<MapDto>(maps)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptySnapshotsKeepTimerManualParityAndOnlyReadCanonicalCatalogues(bool manual)
    {
        if (manual)
        {
            await CreateSut().RunMapRedirectSyncManual(new DefaultHttpContext().Request);
        }
        else
        {
            await CreateSut().RunMapRedirectSync(null);
        }

        _mapRedirectRepositoryMock.Verify(x => x.GetSnapshotForGame("cod4", It.IsAny<CancellationToken>()), Times.Once);
        _mapRedirectRepositoryMock.Verify(x => x.GetSnapshotForGame("cod5", It.IsAny<CancellationToken>()), Times.Once);
        Maps.Verify(x => x.GetMaps(GameType.CallOfDuty4x, It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Maps.Verify(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_auditLoggerMock.Invocations);
    }

    [Fact]
    public async Task SameCountReplacementUsesCanonicalRecursiveNamesUrlsAndVersions()
    {
        var file = RedirectDtoFactory.CreateFile("redirect/cod4/usermaps/mp_test/nested/map name.ff", version: "new-version");
        ConfigureSnapshot(file);
        ConfigureRepository(new MapFileDto("nested/map name.ff", file.DirectUrl!) { RedirectVersion = "old-version" });

        await CreateSut().RunMapRedirectSync(null);

        Maps.Verify(x => x.UpdateMaps(It.Is<List<EditMapDto>>(updates =>
            updates.Count == 1 && updates[0].ReplaceMapFiles && updates[0].MapFiles.Count == 1 &&
            updates[0].MapFiles[0].FileName == "nested/map name.ff" &&
            updates[0].MapFiles[0].Url == file.DirectUrl &&
            updates[0].MapFiles[0].RedirectVersion == "new-version"), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(_auditLoggerMock.Invocations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TimerAndManualTriggersForwardCancellationToRedirectReadsAndRepositoryReadsAndWrites(bool manual, bool update)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var file = RedirectDtoFactory.CreateFile(version: "new");
        ConfigureSnapshot(file);
        if (update)
        {
            ConfigureRepository(new MapFileDto(file.Name, file.DirectUrl!) { RedirectVersion = "old" });
        }

        if (manual)
        {
            await CreateSut().RunMapRedirectSyncManual(new DefaultHttpContext().Request, token);
        }
        else
        {
            await CreateSut().RunMapRedirectSync(null, token);
        }

        _mapRedirectRepositoryMock.Verify(x => x.GetSnapshotForGame("cod4", token), Times.Once);
        _mapRedirectRepositoryMock.Verify(x => x.GetSnapshotForGame("cod5", token), Times.Once);
        Maps.Verify(x => x.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(), token),
            Times.Exactly(update ? 3 : 2));
        Maps.Verify(x => x.CreateMaps(It.Is<List<CreateMapDto>>(maps =>
            maps.All(map => map.GameType == GameType.CallOfDuty4)), token), update ? Times.Never() : Times.Once());
        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), token), update ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringPagingStopsBeforeFurtherReadsWritesOrSuccessAudits(bool manual)
    {
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        ConfigureSnapshot(RedirectDtoFactory.CreateFile());
        Maps.Setup(x => x.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(), token))
            .Returns((GameType? game, string[]? names, MapsFilter? filter, string? search, int skip, int take,
                MapsOrder? order, CancellationToken _) =>
            {
                if (skip == 0)
                {
                    return Task.FromResult(Collection([StoredMap(GameType.CallOfDuty4, [])]));
                }
                cancellation.Cancel();
                return Task.FromResult(Collection([]));
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manual
            ? CreateSut().RunMapRedirectSyncManual(new DefaultHttpContext().Request, token)
            : CreateSut().RunMapRedirectSync(null, token));

        _mapRedirectRepositoryMock.Verify(x => x.GetSnapshotForGame(It.IsAny<string>(), token), Times.Once);
        Maps.Verify(x => x.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
            It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(), token), Times.Exactly(2));
        Maps.Verify(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_auditLoggerMock.Invocations);
    }

    [Fact]
    public async Task UnchangedFilesAreIdempotent()
    {
        var file = RedirectDtoFactory.CreateFile();
        ConfigureSnapshot(file);
        ConfigureRepository(new MapFileDto(file.Name, file.DirectUrl!) { RedirectVersion = file.Version });

        await CreateSut().RunMapRedirectSync(null);

        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_auditLoggerMock.Invocations);
    }

    [Fact]
    public async Task EmptyMapManifestExplicitlyClearsTheFinalFile()
    {
        ConfigureSnapshot();
        ConfigureRepository(new MapFileDto("mp_test.ff", $"{DirectOrigin}/redirect/cod4/usermaps/mp_test/mp_test.ff"));

        await CreateSut().RunMapRedirectSync(null);

        Maps.Verify(x => x.UpdateMaps(It.Is<List<EditMapDto>>(updates =>
            updates.Count == 1 && updates[0].ReplaceMapFiles && updates[0].MapFiles.Count == 0),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentMapsRetainExternalMirrorsAndRemainIdempotentWithEmptyOrPopulatedManifests(bool empty)
    {
        var file = RedirectDtoFactory.CreateFile();
        var external = new MapFileDto(file.Name, "https://elsewhere.invalid/external.ff");
        ConfigureSnapshot(empty ? [] : [file]);
        ConfigureRepository(new MapFileDto(file.Name, file.DirectUrl!) { RedirectVersion = "old" }, external);
        var saved = new List<EditMapDto>();
        Maps.Setup(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()))
            .Callback<List<EditMapDto>, CancellationToken>((updates, _) => saved.AddRange(updates))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));

        await CreateSut().RunMapRedirectSync(null);

        Assert.Single(saved);
        Assert.All(saved, update =>
        {
            Assert.True(update.ReplaceMapFiles);
            Assert.Contains(external, update.MapFiles);
            Assert.Equal(empty ? 1 : 2, update.MapFiles.Count);
        });
        ConfigureRepository(saved[0].MapFiles.AsEnumerable().Reverse().ToArray());
        Maps.Invocations.Clear();

        await CreateSut().RunMapRedirectSync(null);

        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(_auditLoggerMock.Invocations);
    }

    [Fact]
    public async Task DeletedMapFolderRemovesOnlyItsManagedRedirectReferences()
    {
        ConfigureRepository(
            new MapFileDto("mp_test.ff", $"{DirectOrigin}/redirect/cod4/usermaps/mp_test/mp_test.ff"),
            new MapFileDto("external.ff", "https://elsewhere.invalid/external.ff"));

        await CreateSut().RunMapRedirectSync(null);

        Maps.Verify(x => x.UpdateMaps(It.Is<List<EditMapDto>>(updates =>
            updates.Count == 1 && updates[0].ReplaceMapFiles && updates[0].MapFiles.Count == 1 &&
            updates[0].MapFiles[0].FileName == "external.ff"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RepositoryFailureAfterTheFirstPageNeverMutatesOrAuditsPartialData()
    {
        ConfigureSnapshot(RedirectDtoFactory.CreateFile());
        Maps.SetupSequence(x => x.GetMaps(It.IsAny<GameType?>(), It.IsAny<string[]?>(), It.IsAny<MapsFilter?>(),
                It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<MapsOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Collection([StoredMap(GameType.CallOfDuty4, [])]))
            .ReturnsAsync(new ApiResult<CollectionModel<MapDto>>(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RunMapRedirectSync(null));

        Maps.Verify(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Maps.Verify(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_auditLoggerMock.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRepositoryWritesNeverEmitSuccessAudits(bool update)
    {
        var file = RedirectDtoFactory.CreateFile(version: "new");
        ConfigureSnapshot(file);
        if (update)
        {
            ConfigureRepository(new MapFileDto(file.Name, file.DirectUrl!) { RedirectVersion = "old" });
        }
        Maps.Setup(x => x.CreateMaps(It.IsAny<List<CreateMapDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.ServiceUnavailable));
        Maps.Setup(x => x.UpdateMaps(It.IsAny<List<EditMapDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSut().RunMapRedirectSync(null));

        Assert.Empty(_auditLoggerMock.Invocations);
    }
}
