using Microsoft.Extensions.Logging;
using Moq;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class BanFileMonitorTests
{
    private readonly Mock<ILogger<BanFileMonitor>> _loggerMock = new();
    private readonly Mock<IBanFilesRepository> _banFilesRepositoryMock = new();
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> _auditLoggerMock = new();

    public BanFileMonitorTests()
    {
        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    private BanFileMonitor CreateSut() => new(
        _loggerMock.Object,
        _banFilesRepositoryMock.Object,
        _jobTelemetryMock.Object,
        _auditLoggerMock.Object);

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
            _banFilesRepositoryMock.Object,
            _jobTelemetryMock.Object,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullBanFilesRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            null!,
            _jobTelemetryMock.Object,
            _auditLoggerMock.Object));
    }

    [Fact]
    public void Constructor_NullTelemetryClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _banFilesRepositoryMock.Object,
            null!,
            _auditLoggerMock.Object));
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
