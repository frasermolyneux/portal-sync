using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MX.Observability.ApplicationInsights.Jobs;
using MX.Platform.Monitoring;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class ProcessHealthTests
{
    private const string Job = "RunMapImageSync";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingWritesReloadTheETagAndRetainPendingFactsWhenRetriesAreExhausted(bool exhaustRetries)
    {
        var now = DateTimeOffset.UtcNow;
        var original = new JobHealthState(Job, now.AddHours(-2), now.AddHours(-2), null, "success", 1, 0);
        var incoming = new JobHealthState(Job, now.AddHours(-1), now.AddHours(-1), null, "success", 1, 0);
        var concurrent = new JobHealthState(Job, now, original.LastSuccess, now, "failed", 1, 1);
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        container.Setup(client => client.GetBlobClient($"{Job}.json")).Returns(blob.Object);
        var reads = 0;
        blob.Setup(client => client.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response.FromValue(BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromObjectAsJson(++reads == 1 ? original : concurrent),
                details: BlobsModelFactory.BlobDownloadDetails(eTag: new ETag(reads == 1 ? "original" : "concurrent"))),
                Mock.Of<Response>()));
        var writes = new List<(JobHealthState State, ETag? ETag)>();
        blob.Setup(client => client.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns((BinaryData content, BlobUploadOptions options, CancellationToken _) =>
            {
                writes.Add((content.ToObjectFromJson<JobHealthState>()!, options.Conditions.IfMatch));
                return exhaustRetries || writes.Count == 1
                    ? Task.FromException<Response<BlobContentInfo>>(new RequestFailedException(412, "ETag changed."))
                    : Task.FromResult(Mock.Of<Response<BlobContentInfo>>());
            });
        var store = new BlobJobHealthStore(container.Object);
        if (exhaustRetries)
        {
            await Assert.ThrowsAsync<RequestFailedException>(() => store.RecordAsync(incoming, default));
            var retained = await store.ReadAsync(Job, default);
            Assert.Equal(incoming.LastSuccess, retained!.LastSuccess);
            Assert.Equal(concurrent.LastFailure, retained.LastFailure);
        }
        else
        {
            await store.RecordAsync(incoming, default);
        }
        Assert.Equal(exhaustRetries ? 4 : 2, writes.Count);
        Assert.Equal(new ETag("original"), writes[0].ETag);
        Assert.Equal(new ETag("concurrent"), writes[1].ETag);
        Assert.Equal("failed", writes[^1].State.Outcome);
        Assert.Equal(incoming.LastSuccess, writes[^1].State.LastSuccess);
        Assert.Equal(concurrent.LastFailure, writes[^1].State.LastFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneUnreadableDurableJobDoesNotSuppressOtherJobsOrAggregateFeeds(bool transportRetries)
    {
        var now = DateTimeOffset.UtcNow;
        var store = new Mock<IJobHealthStore>();
        store.Setup(target => target.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string name, CancellationToken _) => name == Job
                ? Task.FromException<JobHealthState?>(transportRetries
                    ? new AggregateException(new HttpRequestException("Transport retries exhausted."))
                    : new InvalidDataException("Invalid durable record."))
                : Task.FromResult<JobHealthState?>(new(name, now, now, null, "success", 1, 0)));
        var channel = new Mock<ITelemetryChannel>();
        var events = new List<EventTelemetry>();
        channel.Setup(target => target.Send(It.IsAny<ITelemetry>()))
            .Callback<ITelemetry>(item => events.Add(Assert.IsType<EventTelemetry>(item)));
        using var configuration = new TelemetryConfiguration
        {
            ConnectionString = "InstrumentationKey=11111111-1111-1111-1111-111111111111",
            TelemetryChannel = channel.Object
        };
        var client = new TelemetryClient(configuration);
        var projection = new ProcessHealthProjection(store.Object, new ProcessHealthTelemetry(client), client,
            NullLogger<ProcessHealthProjection>.Instance);

        await projection.PublishProcessHealth(new(), default);

        Assert.Equal(9, events.Count);
        Assert.Equal("unknown", Assert.Single(events, item =>
            item.Name == "ProcessJobSnapshot" && item.Properties["jobName"] == Job).Properties["status"]);
        Assert.Equal("operational", Assert.Single(events, item => item.Name == "ProcessHealthSnapshot"
            && item.Properties["checkId"] == "xi.process.ban-sync.generation").Properties["status"]);
        Assert.Equal("unknown", Assert.Single(events, item => item.Name == "ProcessHealthSnapshot"
            && item.Properties["checkId"] == "xi.process.portal-sync.jobs").Properties["status"]);
    }

    [Fact]
    public async Task FailedPersistenceCannotHideAKnownFailureBehindAnOlderDurableSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var succeeded = new JobHealthState(Job, now.AddHours(-1), now.AddHours(-1), null, "success", 1, 0);
        var failed = new JobHealthState(Job, now, null, now, "failed", 1, 1);
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        container.Setup(client => client.GetBlobClient($"{Job}.json")).Returns(blob.Object);
        container.Setup(client => client.CreateIfNotExistsAsync(PublicAccessType.None,
                It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Storage unavailable."));
        blob.Setup(client => client.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobDownloadResult(
                content: BinaryData.FromObjectAsJson(succeeded)), Mock.Of<Response>()));
        var store = new BlobJobHealthStore(container.Object);
        await Assert.ThrowsAsync<RequestFailedException>(() => store.RecordAsync(failed, default));
        var result = await store.ReadAsync(Job, default);
        Assert.Equal("failed", result!.Outcome);
        Assert.Equal(succeeded.LastSuccess, result.LastSuccess);
        Assert.Equal(failed.LastFailure, result.LastFailure);
    }

    [Fact]
    public void WeeklyDeadlineUsesOriginalDurableCompletionAndNeverFabricatesAnInitialSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var completed = now.AddDays(-7);
        var state = new JobHealthState(Job, completed, completed, null, "success", 1, 0);
        Assert.Equal("unknown", ProcessHealthProjection.Evaluate(null, TimeSpan.FromDays(8), now));
        Assert.Equal("operational", ProcessHealthProjection.Evaluate(state, TimeSpan.FromDays(8), now));
        Assert.Equal("degraded", ProcessHealthProjection.Evaluate(state, TimeSpan.FromDays(8), now.AddDays(2)));
        Assert.Throws<InvalidDataException>(() => ProcessHealthProjection.Evaluate(state with { Outcome = "invented" },
            TimeSpan.FromDays(8), now));
    }

    [Fact]
    public void OutOfOrderCompletionsPreserveLatestFailureAndPreviousLastSuccess()
    {
        var now = DateTimeOffset.UtcNow;
        var succeeded = new JobHealthState(Job, now.AddHours(-1), now.AddHours(-1), null, "success", 1, 0);
        var failed = new JobHealthState(Job, now, null, now, "failed", 1, 1);
        var state = BlobJobHealthStore.Merge(failed, succeeded);
        Assert.Equal("failed", state.Outcome);
        Assert.Equal(succeeded.LastSuccess, state.LastSuccess);
        Assert.Equal(failed.LastFailure, state.LastFailure);
        Assert.Equal("degraded", ProcessHealthProjection.Evaluate(state, TimeSpan.FromDays(8), now));
        Assert.Equal("failed", BlobJobHealthStore.Merge(failed, succeeded with
        {
            CompletedAt = now,
            LastSuccess = now
        }).Outcome);
    }

    [Fact]
    public async Task PartialFailuresAreRecordedAfterTheExistingFailureAuditWithoutBecomingJobCompletion()
    {
        var inner = new Mock<IJobTelemetry>();
        var operation = new Mock<IJobOperation>();
        var store = new Mock<IJobHealthStore>();
        inner.Setup(telemetry => telemetry.StartJob(Job, null)).Returns(operation.Object);
        var telemetry = new PersistentJobTelemetry(inner.Object, store.Object, NullLogger<PersistentJobTelemetry>.Instance);
        var failure = new PartialJobFailureException(4, [new IOException("One game failed.")]);
        await Assert.ThrowsAsync<PartialJobFailureException>(() => telemetry.ExecuteAsync(Job,
            () => Task.FromException(failure)));
        operation.Verify(job => job.FailAsync(failure, null), Times.Once);
        operation.Verify(job => job.Complete(It.IsAny<Dictionary<string, string>?>()), Times.Never);
        store.Verify(target => target.RecordAsync(It.Is<JobHealthState>(state =>
            state.Outcome == "partial_failure" && state.Expected == 4 && state.Failed == 1
            && state.LastSuccess == null && state.LastFailure != null), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("degraded", ProcessHealthTelemetry.Classify(1, 0, 1, 0));
        Assert.Equal("outage", ProcessHealthTelemetry.Classify(1, 0, 1, 0, true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(null)]
    public async Task TelemetryStorageFailureDoesNotRetrySuccessfulBusinessEffects(bool? invalidRecord)
    {
        var inner = new Mock<IJobTelemetry>();
        var operation = new Mock<IJobOperation>();
        var store = new Mock<IJobHealthStore>();
        inner.Setup(telemetry => telemetry.StartJob(Job, null)).Returns(operation.Object);
        store.Setup(target => target.RecordAsync(It.IsAny<JobHealthState>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(invalidRecord switch
            {
                true => new InvalidDataException("Invalid durable state."),
                false => new RequestFailedException(503, "Storage unavailable."),
                null => new AggregateException(new HttpRequestException("Transport retries exhausted."))
            });
        var telemetry = new PersistentJobTelemetry(inner.Object, store.Object, NullLogger<PersistentJobTelemetry>.Instance);
        var runs = 0;
        await telemetry.ExecuteAsync(Job, () => { runs++; return Task.CompletedTask; });
        Assert.Equal(1, runs);
        operation.Verify(job => job.Complete(null), Times.Once);
        operation.Verify(job => job.FailAsync(It.IsAny<Exception>(), It.IsAny<Dictionary<string, string>?>()), Times.Never);
    }

    [Fact]
    public void UnexpectedErrorsInsideTransportAggregatesAreNotSilenced()
    {
        Assert.False(BlobJobHealthStore.IsStorageFailure(new AggregateException(
            new HttpRequestException("Transport failure."), new InvalidOperationException("Programming error."))));
    }
}
