using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using BanFileMonitorOrder = XtremeIdiots.Portal.Repository.Abstractions.Constants.V1.BanFileMonitorOrder;
using XtremeIdiots.Portal.Sync.App.Helpers;
using XtremeIdiots.Portal.Sync.App.Interfaces;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class BanFileMonitorTests
{
    private readonly Mock<ILogger<BanFileMonitor>> _loggerMock = new();
    private readonly Mock<IFtpHelper> _ftpHelperMock = new();
    private readonly Mock<IBanFileIngest> _banFileIngestMock = new();
    private readonly Mock<IBanFilesRepository> _banFilesRepositoryMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration());

    private BanFileMonitor CreateSut() => new(
        _loggerMock.Object,
        _ftpHelperMock.Object,
        _banFileIngestMock.Object,
        _banFilesRepositoryMock.Object,
        _repositoryApiClientMock.Object,
        _telemetryClient);

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            null!,
            _ftpHelperMock.Object,
            _banFileIngestMock.Object,
            _banFilesRepositoryMock.Object,
            _repositoryApiClientMock.Object,
            _telemetryClient));
    }

    [Fact]
    public void Constructor_NullFtpHelper_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            null!,
            _banFileIngestMock.Object,
            _banFilesRepositoryMock.Object,
            _repositoryApiClientMock.Object,
            _telemetryClient));
    }

    [Fact]
    public void Constructor_NullBanFileIngest_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _ftpHelperMock.Object,
            null!,
            _banFilesRepositoryMock.Object,
            _repositoryApiClientMock.Object,
            _telemetryClient));
    }

    [Fact]
    public void Constructor_NullBanFilesRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _ftpHelperMock.Object,
            _banFileIngestMock.Object,
            null!,
            _repositoryApiClientMock.Object,
            _telemetryClient));
    }

    [Fact]
    public void Constructor_NullRepositoryApiClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _ftpHelperMock.Object,
            _banFileIngestMock.Object,
            _banFilesRepositoryMock.Object,
            null!,
            _telemetryClient));
    }

    [Fact]
    public void Constructor_NullTelemetryClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _ftpHelperMock.Object,
            _banFileIngestMock.Object,
            _banFilesRepositoryMock.Object,
            _repositoryApiClientMock.Object,
            null!));
    }

    [Fact]
    public async Task ImportLatestBanFiles_WhenApiReturnsFailure_ThrowsApplicationException()
    {
        Mock.Get(_repositoryApiClientMock.Object.BanFileMonitors.V1)
            .Setup(x => x.GetBanFileMonitors(It.IsAny<GameType[]?>(), It.IsAny<Guid[]?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BanFileMonitorOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<BanFileMonitorDto>>(System.Net.HttpStatusCode.InternalServerError));

        var sut = CreateSut();

        await Assert.ThrowsAsync<ApplicationException>(() => sut.ImportLatestBanFiles(null));
    }

    [Fact]
    public async Task ImportLatestBanFiles_WhenApiReturnsEmptyList_CompletesSuccessfully()
    {
        var response = new ApiResponse<CollectionModel<BanFileMonitorDto>>(
            new CollectionModel<BanFileMonitorDto>(new List<BanFileMonitorDto>()));

        Mock.Get(_repositoryApiClientMock.Object.BanFileMonitors.V1)
            .Setup(x => x.GetBanFileMonitors(It.IsAny<GameType[]?>(), It.IsAny<Guid[]?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BanFileMonitorOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<BanFileMonitorDto>>(System.Net.HttpStatusCode.OK, response));

        var sut = CreateSut();

        await sut.ImportLatestBanFiles(null);

        Mock.Get(_repositoryApiClientMock.Object.BanFileMonitors.V1)
            .Verify(x => x.GetBanFileMonitors(It.IsAny<GameType[]?>(), It.IsAny<Guid[]?>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<BanFileMonitorOrder?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateLatestBansFile_CallsRegenerateBanFileForEachGameType()
    {
        var sut = CreateSut();

        await sut.GenerateLatestBansFile(null);

        _banFilesRepositoryMock.Verify(
            x => x.RegenerateBanFileForGame(It.IsAny<GameType>()),
            Times.Exactly(3));
    }
}
