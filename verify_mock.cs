using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;

var mock = new Mock<HealthCheckService>();
var healthReport = new HealthReport(
    new Dictionary<string, HealthReportEntry>(),
    HealthStatus.Healthy,
    TimeSpan.Zero);

// This is what the test does - mock with parameters
mock.Setup(h => h.CheckHealthAsync(It.IsAny<Func<HealthCheckRegistration, bool>>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync(healthReport);

// This is what the production code does - call without parameters
var result = await mock.Object.CheckHealthAsync();

Console.WriteLine($"Result: {result.Status}");
