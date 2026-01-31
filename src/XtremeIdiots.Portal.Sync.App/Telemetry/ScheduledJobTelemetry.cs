using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;

namespace XtremeIdiots.Portal.Sync.App.Telemetry;

public class ScheduledJobTelemetry(TelemetryClient telemetryClient, string jobName, Dictionary<string, string>? additionalProperties = null)
{
    private readonly TelemetryClient telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
    private readonly string jobName = jobName ?? throw new ArgumentNullException(nameof(jobName));
    private readonly Stopwatch stopwatch = new();
    private readonly Dictionary<string, string> properties = new()
    {
        ["JobName"] = jobName,
        ["StartTime"] = DateTime.UtcNow.ToString("o")
    };

    public void TrackJobStart()
    {
        if (additionalProperties != null)
        {
            foreach (var kvp in additionalProperties)
            {
                properties[kvp.Key] = kvp.Value;
            }
        }

        stopwatch.Start();
        telemetryClient.TrackEvent($"{jobName}_Started", properties);
    }

    public void TrackJobSuccess(Dictionary<string, string>? additionalMetrics = null)
    {
        stopwatch.Stop();
        Dictionary<string, string> successProperties = new(properties)
        {
            ["Status"] = "Success",
            ["DurationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
            ["EndTime"] = DateTime.UtcNow.ToString("o")
        };

        if (additionalMetrics != null)
        {
            foreach (var kvp in additionalMetrics)
            {
                successProperties[kvp.Key] = kvp.Value;
            }
        }

        telemetryClient.TrackEvent($"{jobName}_Completed", successProperties);
        
        var metric = new MetricTelemetry($"{jobName}_Duration", stopwatch.ElapsedMilliseconds);
        foreach (var kvp in properties)
        {
            metric.Properties[kvp.Key] = kvp.Value;
        }
        telemetryClient.TrackMetric(metric);
    }

    public void TrackJobFailure(Exception exception, Dictionary<string, string>? additionalProperties = null)
    {
        stopwatch.Stop();
        Dictionary<string, string> failureProperties = new(properties)
        {
            ["Status"] = "Failed",
            ["DurationMs"] = stopwatch.ElapsedMilliseconds.ToString(),
            ["EndTime"] = DateTime.UtcNow.ToString("o"),
            ["ExceptionType"] = exception.GetType().Name,
            ["ExceptionMessage"] = exception.Message
        };

        if (additionalProperties != null)
        {
            foreach (var kvp in additionalProperties)
            {
                failureProperties[kvp.Key] = kvp.Value;
            }
        }

        telemetryClient.TrackEvent($"{jobName}_Failed", failureProperties);
        telemetryClient.TrackException(exception, failureProperties);
    }

    public async Task TrackJobFailureAsync(Exception exception, Dictionary<string, string>? additionalProperties = null)
    {
        TrackJobFailure(exception, additionalProperties);
        
        // Flush telemetry to ensure it's sent before the exception propagates and potentially terminates the function
        await telemetryClient.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public static async Task<T> ExecuteWithTelemetry<T>(
        TelemetryClient telemetryClient,
        string jobName,
        Func<Task<T>> action,
        Dictionary<string, string>? additionalProperties = null)
    {
        var telemetry = new ScheduledJobTelemetry(telemetryClient, jobName, additionalProperties);
        telemetry.TrackJobStart();

        try
        {
            var result = await action();
            telemetry.TrackJobSuccess();
            return result;
        }
        catch (Exception ex)
        {
            await telemetry.TrackJobFailureAsync(ex);
            throw;
        }
    }

    public static async Task ExecuteWithTelemetry(
        TelemetryClient telemetryClient,
        string jobName,
        Func<Task> action,
        Dictionary<string, string>? additionalProperties = null)
    {
        var telemetry = new ScheduledJobTelemetry(telemetryClient, jobName, additionalProperties);
        telemetry.TrackJobStart();

        try
        {
            await action();
            telemetry.TrackJobSuccess();
        }
        catch (Exception ex)
        {
            await telemetry.TrackJobFailureAsync(ex);
            throw;
        }
    }
}
