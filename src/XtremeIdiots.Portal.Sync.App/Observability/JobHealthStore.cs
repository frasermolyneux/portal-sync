using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Collections.Concurrent;

namespace XtremeIdiots.Portal.Sync.App.Observability;

public sealed record JobHealthState(string Name, DateTimeOffset CompletedAt, DateTimeOffset? LastSuccess,
    DateTimeOffset? LastFailure, string Outcome, int Expected, int Failed);

public interface IJobHealthStore
{
    Task<JobHealthState?> ReadAsync(string name, CancellationToken ct);
    Task RecordAsync(JobHealthState state, CancellationToken ct);
}

public sealed class PartialJobFailureException(int expected, IReadOnlyCollection<Exception> failures)
    : AggregateException("Some scheduled job operations failed.", failures)
{
    public int Expected { get; } = expected;
    public int Failed => InnerExceptions.Count;
}

public sealed class BlobJobHealthStore(BlobContainerClient container) : IJobHealthStore
{
    private readonly ConcurrentDictionary<string, JobHealthState> pending = new(StringComparer.Ordinal);

    internal static bool IsStorageFailure(Exception exception) => exception switch
    {
        RequestFailedException or Azure.Identity.AuthenticationFailedException or IOException or InvalidDataException
            or System.Text.Json.JsonException or OperationCanceledException or HttpRequestException
            or System.Net.Sockets.SocketException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsStorageFailure),
        _ => false
    };

    public async Task<JobHealthState?> ReadAsync(string name, CancellationToken ct)
    {
        try
        {
            var response = await container.GetBlobClient(BlobName(name)).DownloadContentAsync(ct).ConfigureAwait(false);
            var state = response.Value.Content.ToObjectFromJson<JobHealthState>()
                ?? throw new InvalidDataException("A durable job outcome is empty.");
            Validate(state, name, DateTimeOffset.UtcNow);
            return pending.TryGetValue(name, out var awaitingPersistence) ? Merge(state, awaitingPersistence) : state;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return pending.GetValueOrDefault(name);
        }
    }

    public async Task RecordAsync(JobHealthState state, CancellationToken ct)
    {
        Validate(state, state.Name, DateTimeOffset.UtcNow);
        state = pending.AddOrUpdate(state.Name, state, (_, previous) => Merge(previous, state));
        await PersistAsync(state, ct).ConfigureAwait(false);
        pending.TryRemove(new KeyValuePair<string, JobHealthState>(state.Name, state));
    }

    private async Task PersistAsync(JobHealthState state, CancellationToken ct)
    {
        await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
        var blob = container.GetBlobClient(BlobName(state.Name));
        for (var attempt = 0; ; attempt++)
        {
            JobHealthState? current = null;
            var conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            try
            {
                var download = await blob.DownloadContentAsync(ct).ConfigureAwait(false);
                current = download.Value.Content.ToObjectFromJson<JobHealthState>()
                    ?? throw new InvalidDataException("A durable job outcome is empty.");
                Validate(current, state.Name, DateTimeOffset.UtcNow);
                conditions = new BlobRequestConditions { IfMatch = download.Value.Details.ETag };
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // The first completed execution creates the durable record.
            }
            var merged = Merge(current, state);
            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(merged),
                    new BlobUploadOptions { Conditions = conditions }, ct).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException ex) when ((ex.Status == 409 || ex.Status == 412) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    internal static JobHealthState Merge(JobHealthState? current, JobHealthState incoming)
    {
        if (current is null)
        {
            return incoming;
        }
        if (current.Name != incoming.Name)
        {
            throw new InvalidDataException("Cannot merge outcomes belonging to different jobs.");
        }
        var currentWinsTie = (OutcomeRank(current.Outcome), current.Failed, current.Expected)
            .CompareTo((OutcomeRank(incoming.Outcome), incoming.Failed, incoming.Expected)) >= 0;
        var latest = current.CompletedAt > incoming.CompletedAt
            || (current.CompletedAt == incoming.CompletedAt && currentWinsTie) ? current : incoming;
        return latest with
        {
            LastSuccess = Max(current.LastSuccess, incoming.LastSuccess),
            LastFailure = Max(current.LastFailure, incoming.LastFailure)
        };
    }

    private static int OutcomeRank(string outcome) => outcome switch
    {
        "success" => 0,
        "partial_failure" => 1,
        "failed" => 2,
        _ => throw new InvalidDataException("Cannot rank an invalid durable job outcome.")
    };

    internal static void Validate(JobHealthState state, string expectedName, DateTimeOffset now)
    {
        _ = BlobName(expectedName);
        var validOutcome = state.Outcome switch
        {
            "success" => state.Failed == 0 && state.LastSuccess == state.CompletedAt,
            "failed" => state.Failed == state.Expected && state.LastFailure == state.CompletedAt,
            "partial_failure" => state.Failed > 0 && state.Failed < state.Expected && state.LastFailure == state.CompletedAt,
            _ => false
        };
        if (state.Name != expectedName || state.CompletedAt == default || state.CompletedAt > now.AddMinutes(5)
            || state.LastSuccess > state.CompletedAt || state.LastFailure > state.CompletedAt
            || state.Expected <= 0 || state.Failed < 0 || !validOutcome)
        {
            throw new InvalidDataException("Invalid durable job identity, lifecycle outcome or timestamps.");
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null || left > right ? left : right;

    private static string BlobName(string name) =>
        MX.Platform.Monitoring.ProcessHealthTelemetry.Policy.GetProperty("jobs").EnumerateArray()
            .Any(job => job.GetProperty("name").GetString() == name)
            ? $"{name}.json" : throw new ArgumentException("Unknown scheduled job.", nameof(name));
}
