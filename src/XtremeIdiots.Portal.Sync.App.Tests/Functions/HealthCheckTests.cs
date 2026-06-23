using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.AspNetCore.Http;
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
    public async Task RunReady_WhenHealthy_Returns200WithHealthyStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Healthy,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        var sut = CreateSut();
        var result = await sut.RunReady(null!, context.Object);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status200OK, objectResult.StatusCode);

        var statusProperty = objectResult.Value?.GetType().GetProperty("status");
        Assert.NotNull(statusProperty);
        Assert.Equal("Healthy", statusProperty.GetValue(objectResult.Value));

        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunReady_WhenDegraded_Returns503WithDegradedStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Degraded,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        var sut = CreateSut();
        var result = await sut.RunReady(null!, context.Object);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        var statusProperty = objectResult.Value?.GetType().GetProperty("status");
        Assert.NotNull(statusProperty);
        Assert.Equal("Degraded", statusProperty.GetValue(objectResult.Value));

        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunReady_WhenUnhealthy_Returns503WithUnhealthyStatus()
    {
        var healthReport = new HealthReport(
            new Dictionary<string, HealthReportEntry>(),
            HealthStatus.Unhealthy,
            TimeSpan.Zero);

        _healthCheckServiceMock
            .Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(healthReport);

        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        var sut = CreateSut();
        var result = await sut.RunReady(null!, context.Object);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, objectResult.StatusCode);

        var statusProperty = objectResult.Value?.GetType().GetProperty("status");
        Assert.NotNull(statusProperty);
        Assert.Equal("Unhealthy", statusProperty.GetValue(objectResult.Value));

        _healthCheckServiceMock.Verify(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void RunLive_ReturnsHealthyStatus()
    {
        var sut = CreateSut();
        var result = sut.RunLive(null!);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var statusProperty = okResult.Value?.GetType().GetProperty("status");
        Assert.NotNull(statusProperty);
        Assert.Equal("Healthy", statusProperty.GetValue(okResult.Value));
    }
}
