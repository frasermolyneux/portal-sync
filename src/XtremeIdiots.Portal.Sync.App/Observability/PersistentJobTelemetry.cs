using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Jobs;

namespace XtremeIdiots.Portal.Sync.App.Observability;

public sealed class PersistentJobTelemetry(IJobTelemetry inner, IJobHealthStore store,
    ILogger<PersistentJobTelemetry> logger) : IJobTelemetry
{
    public IJobOperation StartJob(string jobName, Dictionary<string, string>? properties = null) =>
        new Operation(jobName, inner.StartJob(jobName, properties), store, logger);

    public async Task<T> ExecuteAsync<T>(string jobName, Func<Task<T>> action, Dictionary<string, string>? properties = null)
    {
        await using var operation = StartJob(jobName, properties);
        try
        {
            var result = await action().ConfigureAwait(false);
            operation.Complete();
            return result;
        }
        catch (Exception exception)
        {
            await operation.FailAsync(exception).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ExecuteAsync(string jobName, Func<Task> action, Dictionary<string, string>? properties = null)
    {
        await ExecuteAsync(jobName, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        }, properties).ConfigureAwait(false);
    }

    private sealed class Operation(string name, IJobOperation inner, IJobHealthStore store,
        ILogger logger) : IJobOperation
    {
        private JobHealthState? state;
        private bool disposed;

        public void Complete(Dictionary<string, string>? additionalMetrics = null)
        {
            if (state is not null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            state = new(name, now, now, null, "success", 1, 0);
            inner.Complete(additionalMetrics);
        }

        public async Task FailAsync(Exception exception, Dictionary<string, string>? additionalProperties = null)
        {
            if (state is not null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var partial = exception as PartialJobFailureException;
            state = new(name, now, null, now,
                partial is not null && partial.Failed < partial.Expected ? "partial_failure" : "failed",
                partial?.Expected ?? 1, partial?.Failed ?? 1);
            await inner.FailAsync(exception, additionalProperties).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            if (state is null)
            {
                Complete();
            }
            await inner.DisposeAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await store.RecordAsync(state!, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (BlobJobHealthStore.IsStorageFailure(exception))
            {
                // Retrying business side effects because their telemetry write failed is unsafe.
                logger.LogError(exception, "Could not persist the outcome of {JobName}; its pending outcome survives only in this process.", name);
            }
        }
    }
}
