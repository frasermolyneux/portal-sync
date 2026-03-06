using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App.Tests.Telemetry;

public class ScheduledJobTelemetryTests
{
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration());

    [Fact]
    public async Task ExecuteWithTelemetry_SuccessfulAction_CompletesWithoutException()
    {
        var actionInvoked = false;

        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            _telemetryClient,
            "TestJob",
            async () =>
            {
                actionInvoked = true;
                await Task.CompletedTask;
            });

        Assert.True(actionInvoked);
    }

    [Fact]
    public async Task ExecuteWithTelemetry_FailedAction_ThrowsException()
    {
        var expectedException = new InvalidOperationException("Test failure");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScheduledJobTelemetry.ExecuteWithTelemetry(
                _telemetryClient,
                "TestJob",
                () => throw expectedException));

        Assert.Same(expectedException, thrown);
    }

    [Fact]
    public async Task ExecuteWithTelemetry_WithReturnValue_ReturnsResult()
    {
        var result = await ScheduledJobTelemetry.ExecuteWithTelemetry(
            _telemetryClient,
            "TestJob",
            async () =>
            {
                await Task.CompletedTask;
                return 42;
            });

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ExecuteWithTelemetry_WithReturnValue_FailedAction_ThrowsException()
    {
        var expectedException = new ApplicationException("Test failure");

        await Assert.ThrowsAsync<ApplicationException>(() =>
            ScheduledJobTelemetry.ExecuteWithTelemetry<int>(
                _telemetryClient,
                "TestJob",
                () => throw expectedException));
    }

    [Fact]
    public void TrackJobStart_DoesNotThrow()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
    }

    [Fact]
    public void TrackJobSuccess_AfterStart_DoesNotThrow()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
        sut.TrackJobSuccess();
    }

    [Fact]
    public void TrackJobFailure_AfterStart_DoesNotThrow()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
        sut.TrackJobFailure(new Exception("test"));
    }

    [Fact]
    public void TrackJobStart_WithAdditionalProperties_DoesNotThrow()
    {
        var props = new Dictionary<string, string> { ["key1"] = "value1" };
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob", props);
        sut.TrackJobStart();
    }

    [Fact]
    public void TrackJobSuccess_WithAdditionalMetrics_DoesNotThrow()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
        sut.TrackJobSuccess(new Dictionary<string, string> { ["RecordCount"] = "10" });
    }

    [Fact]
    public void TrackJobFailure_WithAdditionalProperties_DoesNotThrow()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
        sut.TrackJobFailure(new Exception("test"), new Dictionary<string, string> { ["Context"] = "retry" });
    }

    [Fact]
    public async Task TrackJobFailureAsync_FlushesWithoutThrowing()
    {
        var sut = new ScheduledJobTelemetry(_telemetryClient, "TestJob");
        sut.TrackJobStart();
        await sut.TrackJobFailureAsync(new Exception("async test"));
    }

    [Fact]
    public void Constructor_NullTelemetryClient_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ScheduledJobTelemetry(null!, "TestJob"));
    }

    [Fact]
    public void Constructor_NullJobName_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ScheduledJobTelemetry(_telemetryClient, null!));
    }
}
