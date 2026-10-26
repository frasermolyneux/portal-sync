using Microsoft.Extensions.Logging;
using Moq;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class BanFileMonitorTests
{
    private readonly Mock<ILogger<BanFileMonitor>> _loggerMock = new();
    private readonly Mock<IBanFilesRepository> _banFilesRepositoryMock = new();
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();

    public BanFileMonitorTests()
    {
        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());

        // Default: any RegenerateBanFileForGame call returns a "skipped, empty" result
        // so the function trigger path is exercised without requiring blob writes.
        _banFilesRepositoryMock
            .Setup(x => x.RegenerateBanFileForGame(It.IsAny<GameType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GameType gt, CancellationToken _) => new BanFileRegenerationResult
            {
                GameType = gt,
                Skipped = true,
                ActiveBanSetHash = "EMPTY",
                BanSyncLineCount = 0,
                DurationMs = 1
            });
    }

    private BanFileMonitor CreateSut() => new(
        _loggerMock.Object,
        _banFilesRepositoryMock.Object,
        _jobTelemetryMock.Object);

    [Fact]
    public async Task RegenerationFailureStillAttemptsOtherGamesAndCannotEmitSuccessfulJobCompletion()
    {
        _banFilesRepositoryMock.Setup(repository => repository.RegenerateBanFileForGame(GameType.CallOfDuty4, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Regeneration failed."));
        var failure = await Assert.ThrowsAsync<PartialJobFailureException>(() => CreateSut().GenerateLatestBansFile(null));
        Assert.Equal(4, failure.Expected);
        Assert.Equal(1, failure.Failed);
        _banFilesRepositoryMock.Verify(repository => repository.RegenerateBanFileForGame(It.IsAny<GameType>(),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
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
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            null!,
            _banFilesRepositoryMock.Object,
            _jobTelemetryMock.Object));
    }

    [Fact]
    public void Constructor_NullBanFilesRepository_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            null!,
            _jobTelemetryMock.Object));
    }

    [Fact]
    public void Constructor_NullTelemetryClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new BanFileMonitor(
            _loggerMock.Object,
            _banFilesRepositoryMock.Object,
            null!));
    }

    [Fact]
    public async Task GenerateLatestBansFile_CallsRegenerateBanFileForEachGameType()
    {
        var sut = CreateSut();

        await sut.GenerateLatestBansFile(null);

        _banFilesRepositoryMock.Verify(
            x => x.RegenerateBanFileForGame(It.IsAny<GameType>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
    }
}
