using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class HealthCheckTests
{
    private readonly Mock<HealthCheckService> _healthCheckServiceMock = new();

    private HealthCheck CreateSut() => new(_healthCheckServiceMock.Object);

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task Run_WhenHealthy_ReturnsOkWithHealthyStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Healthy,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var sut = CreateSut();
        var result = await sut.Run(null!, null!);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Healthy", okResult.Value);
        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenDegraded_ReturnsOkWithDegradedStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Degraded,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var sut = CreateSut();
        var result = await sut.Run(null!, null!);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Degraded", okResult.Value);
        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WhenUnhealthy_ReturnsOkWithUnhealthyStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Unhealthy,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var sut = CreateSut();
        var result = await sut.Run(null!, null!);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("Unhealthy", okResult.Value);
        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
