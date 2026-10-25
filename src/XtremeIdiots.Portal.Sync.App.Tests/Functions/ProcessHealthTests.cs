using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MX.Observability.ApplicationInsights.Jobs;
using MX.Platform.Monitoring;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class ProcessHealthTests
{
    private const string Job = "RunMapImageSync";

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

    [Fact]
    public async Task TelemetryStorageFailureDoesNotRetrySuccessfulBusinessEffects()
    {
        var inner = new Mock<IJobTelemetry>();
        var operation = new Mock<IJobOperation>();
        var store = new Mock<IJobHealthStore>();
        inner.Setup(telemetry => telemetry.StartJob(Job, null)).Returns(operation.Object);
        store.Setup(target => target.RecordAsync(It.IsAny<JobHealthState>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(503, "Storage unavailable."));
        var telemetry = new PersistentJobTelemetry(inner.Object, store.Object, NullLogger<PersistentJobTelemetry>.Instance);
        var runs = 0;
        await telemetry.ExecuteAsync(Job, () => { runs++; return Task.CompletedTask; });
        Assert.Equal(1, runs);
        operation.Verify(job => job.Complete(null), Times.Once);
        operation.Verify(job => job.FailAsync(It.IsAny<Exception>(), It.IsAny<Dictionary<string, string>?>()), Times.Never);
    }
}
