using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Platform.Monitoring;

namespace XtremeIdiots.Portal.Sync.App.Observability;

public sealed class ProcessHealthProjection(IJobHealthStore store, ProcessHealthTelemetry telemetry, TelemetryClient client,
    ILogger<ProcessHealthProjection> logger)
{
    [Function(nameof(PublishProcessHealth))]
    public async Task PublishProcessHealth([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken ct)
    {
        var jobs = ProcessHealthTelemetry.Policy.GetProperty("jobs").EnumerateArray().ToArray();
        var states = await Task.WhenAll(jobs.Select(async job =>
        {
            var name = job.GetProperty("name").GetString() ?? throw new InvalidDataException("A process job name is missing.");
            try
            {
                return (Job: job, State: await store.ReadAsync(name, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is Azure.RequestFailedException or Azure.Identity.AuthenticationFailedException
                or IOException or System.Text.Json.JsonException)
            {
                logger.LogError(exception, "No reliable durable outcome is available for {JobName}; reporting Unknown.", name);
                return (Job: job, State: null);
            }
        })).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var group in states.GroupBy(item => item.Job.GetProperty("checkId").GetString()!))
        {
            var healthy = 0;
            var failed = 0;
            var unknown = 0;
            var confirmedFailures = 0;
            foreach (var (job, state) in group)
            {
                var maximumAge = TimeSpan.FromSeconds(job.GetProperty("maximumAgeSeconds").GetInt32());
                var outcome = Evaluate(state, maximumAge, now);
                if (outcome == "operational")
                {
                    healthy++;
                }
                else if (outcome == "unknown")
                {
                    unknown++;
                }
                else
                {
                    failed++;
                    if (state?.Outcome == "failed")
                    {
                        confirmedFailures++;
                    }
                }
                var details = new EventTelemetry("ProcessJobSnapshot") { Timestamp = now };
                details.Properties["contractVersion"] = "1";
                details.Properties["siteId"] = "xi";
                details.Properties["jobName"] = job.GetProperty("name").GetString()!;
                details.Properties["status"] = outcome;
                details.Properties["outcome"] = state?.Outcome ?? "unobserved";
                details.Properties["completedAt"] = state?.CompletedAt.ToString("O", CultureInfo.InvariantCulture) ?? "";
                details.Properties["lastSuccess"] = state?.LastSuccess?.ToString("O", CultureInfo.InvariantCulture) ?? "";
                details.Properties["lastFailure"] = state?.LastFailure?.ToString("O", CultureInfo.InvariantCulture) ?? "";
                client.TrackEvent(details);
            }
            var expected = healthy + failed + unknown;
            telemetry.Publish(group.Key, ProcessHealthTelemetry.Classify(expected, healthy, failed, unknown,
                confirmedFailures == expected), now, expected, healthy, failed, unknown);
        }
    }

    internal static string Evaluate(JobHealthState? state, TimeSpan maximumAge, DateTimeOffset now)
    {
        if (state is null)
        {
            return "unknown";
        }

        BlobJobHealthStore.Validate(state, state.Name, now);
        if (state.Outcome is "failed" or "partial_failure")
        {
            return "degraded";
        }

        return state.LastSuccess is { } success && now - success <= maximumAge ? "operational" : "degraded";
    }
}
