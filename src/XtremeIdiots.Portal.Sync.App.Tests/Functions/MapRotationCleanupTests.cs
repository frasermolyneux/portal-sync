using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

[Trait("Category", "Unit")]
public class MapRotationCleanupTests
{
    private readonly Mock<IRepositoryApiClient> repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IJobTelemetry> jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> auditLoggerMock = new();

    public MapRotationCleanupTests()
    {
        jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    [Fact]
    public async Task RunMapRotationCleanup_WhenAssignmentIsStaleRemoving_ReconcilesToRemoved()
    {
        var assignmentId = Guid.NewGuid();
        var oldUpdatedAt = DateTime.UtcNow.AddMinutes(-20);
        var assignments = new CollectionModel<MapRotationServerAssignmentDto>(
        [
            new MapRotationServerAssignmentDto(
                assignmentId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DeploymentState.Removing,
                ActivationState.Inactive,
                null,
                null,
                "server.cfg",
                "sv_maprotation",
                null,
                null,
                oldUpdatedAt.AddHours(-1),
                oldUpdatedAt,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(assignments)));

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var operations = new CollectionModel<MapRotationAssignmentOperationDto>(
        [
            new MapRotationAssignmentOperationDto(
                Guid.NewGuid(),
                assignmentId,
                AssignmentOperationType.Remove,
                AssignmentOperationStatus.Failed,
                null,
                DateTime.UtcNow.AddHours(-2),
                DateTime.UtcNow.AddHours(-2),
                "previous failure")
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetAssignmentOperations(assignmentId, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(operations)));

        var sut = new MapRotationCleanup(
            Mock.Of<ILogger<MapRotationCleanup>>(),
            repositoryApiClientMock.Object,
            jobTelemetryMock.Object,
            auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Verify(x => x.UpdateServerAssignment(
                It.Is<UpdateMapRotationServerAssignmentDto>(dto =>
                    dto.MapRotationServerAssignmentId == assignmentId
                    && dto.DeploymentState == DeploymentState.Removed
                    && dto.UnassignedAt == oldUpdatedAt),
                It.IsAny<CancellationToken>()),
                Times.Once);
    }

    [Fact]
    public async Task RunMapRotationCleanup_WhenStaleRemovingHasRecentInProgressRemove_DoesNotReconcile()
    {
        var assignmentId = Guid.NewGuid();
        var oldUpdatedAt = DateTime.UtcNow.AddMinutes(-20);
        var assignments = new CollectionModel<MapRotationServerAssignmentDto>(
        [
            new MapRotationServerAssignmentDto(
                assignmentId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                DeploymentState.Removing,
                ActivationState.Inactive,
                null,
                null,
                "server.cfg",
                "sv_maprotation",
                null,
                null,
                oldUpdatedAt.AddHours(-1),
                oldUpdatedAt,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(assignments)));

        var operations = new CollectionModel<MapRotationAssignmentOperationDto>(
        [
            new MapRotationAssignmentOperationDto(
                Guid.NewGuid(),
                assignmentId,
                AssignmentOperationType.Remove,
                AssignmentOperationStatus.InProgress,
                "maprot-remove-instance",
                DateTime.UtcNow.AddMinutes(-10),
                null,
                null)
        ]);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetAssignmentOperations(assignmentId, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(operations)));

        var sut = new MapRotationCleanup(
            Mock.Of<ILogger<MapRotationCleanup>>(),
            repositoryApiClientMock.Object,
            jobTelemetryMock.Object,
            auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Verify(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, "success", false, true)]
    [InlineData(true, "failure", false, true)]
    [InlineData(true, "exception", false, true)]
    [InlineData(true, "operations-failure", false, true)]
    [InlineData(true, "recent", false, true)]
    [InlineData(true, "other-recent", false, true)]
    [InlineData(true, "old-in-progress", false, true)]
    [InlineData(true, "audit-exception", false, true)]
    [InlineData(false, "success", true, true)]
    [InlineData(false, "success", false, true)]
    [InlineData(false, "failure", false, true)]
    [InlineData(false, "exception", false, true)]
    [InlineData(false, "audit-exception", false, true)]
    [InlineData(true, "success", false, false)]
    [InlineData(false, "success", false, false)]
    public async Task RunMapRotationCleanup_PreservesLoggingAndSideEffects(bool removing, string outcome, bool hasUnassignedAt, bool loggingEnabled)
    {
        var assignmentId = Guid.NewGuid();
        var oldUpdatedAt = DateTime.UtcNow.AddDays(-3);
        DateTime? unassignedAt = hasUnassignedAt ? oldUpdatedAt : null;
        var exception = new InvalidOperationException("Repository unavailable");
        var statusCode = outcome is "failure" or "operations-failure"
            ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK;
        var assignments = new CollectionModel<MapRotationServerAssignmentDto>(
        [
            new(assignmentId, Guid.NewGuid(), Guid.NewGuid(), removing ? DeploymentState.Removing : DeploymentState.Removed,
                ActivationState.Inactive, null, null, "server.cfg", "sv_maprotation", null, null,
                oldUpdatedAt.AddHours(-1), oldUpdatedAt, unassignedAt)
        ]);
        var api = Mock.Get(repositoryApiClientMock.Object.MapRotations.V1);
        api.Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(System.Net.HttpStatusCode.OK, new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(assignments)));
        var operations = new CollectionModel<MapRotationAssignmentOperationDto>(outcome is "recent" or "other-recent" or "old-in-progress"
            ? [new(Guid.NewGuid(), assignmentId,
                outcome == "other-recent" ? AssignmentOperationType.Sync : AssignmentOperationType.Remove,
                AssignmentOperationStatus.InProgress, "remove-instance",
                DateTime.UtcNow.AddMinutes(outcome == "old-in-progress" ? -120 : -10), null, null)] : []);
        api.Setup(x => x.GetAssignmentOperations(assignmentId, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(
                outcome == "operations-failure" ? statusCode : System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(operations)));
        api.Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(statusCode));
        api.Setup(x => x.DeleteServerAssignment(assignmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(statusCode));
        if (outcome == "exception")
        {
            api.Setup(x => x.UpdateServerAssignment(It.IsAny<UpdateMapRotationServerAssignmentDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
            api.Setup(x => x.DeleteServerAssignment(assignmentId, It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }
        if (outcome == "audit-exception")
        {
            _ = auditLoggerMock.Setup(x => x.LogAudit(It.IsAny<AuditEvent>())).Throws(exception);
        }
        var logger = new Mock<ILogger<MapRotationCleanup>>();
        logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(loggingEnabled);
        var sut = new MapRotationCleanup(logger.Object, repositoryApiClientMock.Object, jobTelemetryMock.Object, auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        var succeeded = outcome is "success" or "other-recent" or "old-in-progress" or "audit-exception";
        api.Verify(x => x.UpdateServerAssignment(It.Is<UpdateMapRotationServerAssignmentDto>(dto =>
            dto.MapRotationServerAssignmentId == assignmentId && dto.DeploymentState == DeploymentState.Removed
            && dto.UnassignedAt == oldUpdatedAt), It.IsAny<CancellationToken>()),
            removing && outcome is not ("operations-failure" or "recent") ? Times.Once() : Times.Never());
        api.Verify(x => x.DeleteServerAssignment(assignmentId, It.IsAny<CancellationToken>()), removing ? Times.Never() : Times.Once());
        Assert.Equal(succeeded ? 1 : 0, auditLoggerMock.Invocations.Count);
        jobTelemetryMock.Verify(x => x.ExecuteAsync(nameof(MapRotationCleanup.RunMapRotationCleanup),
            It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()), Times.Once);
        if (!loggingEnabled)
        {
            Assert.DoesNotContain(logger.Invocations, invocation => invocation.Method.Name == "Log");
            return;
        }
        AssertLog(logger, LogLevel.Information, "Starting map rotation cleanup", null);
        var template = (removing, outcome) switch
        {
            (true, "success" or "other-recent" or "old-in-progress") => "Reconciled stale removing assignment {AssignmentId} to Removed",
            (true, "failure") => "Failed to reconcile stale removing assignment {AssignmentId}. API returned {StatusCode}",
            (true, "exception" or "audit-exception") => "Failed to reconcile stale removing assignment {AssignmentId}",
            (true, "operations-failure") => "Skipping stale removing reconciliation for assignment {AssignmentId} because operations could not be retrieved: {StatusCode}",
            (true, "recent") => "Skipping stale removing reconciliation for assignment {AssignmentId} because a recent in-progress Remove operation exists",
            (false, "success") => "Deleted removed assignment {AssignmentId} (unassigned at {UnassignedAt})",
            (false, "failure") => "Skipping cleanup count/audit for assignment {AssignmentId} because delete failed: {StatusCode}",
            _ => "Failed to delete assignment {AssignmentId}"
        };
        var values = new List<(string, object?)> { ("AssignmentId", assignmentId) };
        if (outcome is "failure" or "operations-failure")
        {
            values.Add(("StatusCode", statusCode));
        }
        if (!removing && outcome == "success")
        {
            values.Add(("UnassignedAt", unassignedAt));
        }
        var level = outcome is "exception" or "audit-exception" ? LogLevel.Error
            : outcome is "failure" or "operations-failure" ? LogLevel.Warning : LogLevel.Information;
        AssertLog(logger, level, template, outcome is "exception" or "audit-exception" ? exception : null, [.. values]);
        AssertLog(logger, LogLevel.Information,
            "Map rotation cleanup completed, reconciled {ReconciledCount} stale removing assignments and deleted {DeletedCount} removed assignments",
            null, ("ReconciledCount", removing && succeeded ? 1 : 0), ("DeletedCount", !removing && succeeded ? 1 : 0));
        Assert.Equal(outcome == "audit-exception" ? 4 : 3,
            logger.Invocations.Count(invocation => invocation.Method.Name == "Log"));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable)]
    [InlineData(System.Net.HttpStatusCode.OK)]
    public async Task RunMapRotationCleanup_WhenAssignmentsUnavailable_LogsWarningAndStops(System.Net.HttpStatusCode statusCode)
    {
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Setup(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(statusCode));
        var logger = new Mock<ILogger<MapRotationCleanup>>();
        logger.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var sut = new MapRotationCleanup(logger.Object, repositoryApiClientMock.Object, jobTelemetryMock.Object, auditLoggerMock.Object);

        await sut.RunMapRotationCleanup(null);

        AssertLog(logger, LogLevel.Information, "Starting map rotation cleanup", null);
        AssertLog(logger, LogLevel.Warning, "Failed to retrieve server assignments for cleanup", null);
        Assert.Equal(2, logger.Invocations.Count(invocation => invocation.Method.Name == "Log"));
        Assert.Empty(auditLoggerMock.Invocations);
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1)
            .Verify(x => x.GetServerAssignments(null, null, null, 0, 100, It.IsAny<CancellationToken>()), Times.Once);
        Mock.Get(repositoryApiClientMock.Object.MapRotations.V1).VerifyNoOtherCalls();
    }

    private static void AssertLog(Mock<ILogger<MapRotationCleanup>> logger, LogLevel level, string template,
        Exception? exception, params (string Key, object? Value)[] values)
    {
        var invocation = Assert.Single(logger.Invocations, call => call.Method.Name == "Log"
            && call.Arguments[2] is IEnumerable<KeyValuePair<string, object?>> entries && entries.Any(pair =>
                pair.Key == "{OriginalFormat}" && Equals(pair.Value, template)));
        Assert.Equal(level, invocation.Arguments[0]);
        Assert.Same(exception, invocation.Arguments[3]);
        var state = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(invocation.Arguments[2]).ToDictionary();
        Assert.Equal(values.Length + 1, state.Count);
        foreach (var (key, value) in values)
        {
            Assert.Equal(value, state[key]);
        }
    }
}
