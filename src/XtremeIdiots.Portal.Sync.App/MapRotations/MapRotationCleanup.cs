using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public partial class MapRotationCleanup(
    ILogger<MapRotationCleanup> logger,
    IRepositoryApiClient repositoryApiClient,
    IJobTelemetry jobTelemetry,
    IAuditLogger auditLogger)
{
    private readonly ILogger<MapRotationCleanup> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly IJobTelemetry jobTelemetry = jobTelemetry ?? throw new ArgumentNullException(nameof(jobTelemetry));
    private readonly IAuditLogger auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

    [Function(nameof(RunMapRotationCleanupManual))]
    public async Task RunMapRotationCleanupManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapRotationCleanup(null).ConfigureAwait(false);
    }

    [Function(nameof(RunMapRotationCleanup))]
    public async Task RunMapRotationCleanup([TimerTrigger("0 0 * * * *")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(
            nameof(RunMapRotationCleanup),
            async () =>
            {
                await ProcessCleanup().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task ProcessCleanup()
    {
        LogCleanupStarting(logger);

        var assignmentsResult = await repositoryApiClient.MapRotations.V1
            .GetServerAssignments(null, null, null, 0, 100).ConfigureAwait(false);

        if (!assignmentsResult.IsSuccess || assignmentsResult.Result?.Data?.Items is null)
        {
            LogServerAssignmentsRetrievalFailed(logger);
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var removingStaleCutoff = DateTime.UtcNow.AddMinutes(-15);
        var activeRemovingOperationGraceCutoff = DateTime.UtcNow.AddHours(-1);
        var deletedCount = 0;
        var reconciledRemovingCount = 0;

        foreach (var assignment in assignmentsResult.Result.Data.Items)
        {
            if (assignment.DeploymentState == DeploymentState.Removing)
            {
                if (await ReconcileStaleRemovingAssignment(
                    assignment,
                    removingStaleCutoff,
                    activeRemovingOperationGraceCutoff).ConfigureAwait(false))
                {
                    reconciledRemovingCount++;
                }

                continue;
            }

            if (assignment.DeploymentState == DeploymentState.Removed
                && await DeleteRemovedAssignment(assignment, cutoff).ConfigureAwait(false))
            {
                deletedCount++;
            }
        }

        LogCleanupCompleted(logger, reconciledRemovingCount, deletedCount);
    }

    private async Task<bool> ReconcileStaleRemovingAssignment(
        MapRotationServerAssignmentDto assignment,
        DateTime removingStaleCutoff,
        DateTime activeRemovingOperationGraceCutoff)
    {
        if (assignment.UpdatedAt >= removingStaleCutoff)
        {
            return false;
        }

        try
        {
            var operationsResult = await repositoryApiClient.MapRotations.V1
                .GetAssignmentOperations(assignment.MapRotationServerAssignmentId, 0, 100)
                .ConfigureAwait(false);

            if (!operationsResult.IsSuccess || operationsResult.Result?.Data?.Items is null)
            {
                LogStaleRemovingOperationsRetrievalFailed(
                    logger,
                    assignment.MapRotationServerAssignmentId,
                    operationsResult.StatusCode);
                return false;
            }

            var hasRecentInProgressRemove = operationsResult.Result.Data.Items.Any(operation =>
                operation.OperationType == AssignmentOperationType.Remove
                && operation.Status == AssignmentOperationStatus.InProgress
                && operation.StartedAt >= activeRemovingOperationGraceCutoff);

            if (hasRecentInProgressRemove)
            {
                LogRecentRemoveOperationFound(logger, assignment.MapRotationServerAssignmentId);
                return false;
            }

            var reconcileResult = await repositoryApiClient.MapRotations.V1
                .UpdateServerAssignment(new UpdateMapRotationServerAssignmentDto(assignment.MapRotationServerAssignmentId)
                {
                    DeploymentState = DeploymentState.Removed,
                    UnassignedAt = assignment.UnassignedAt ?? assignment.UpdatedAt,
                    LastError = "",
                    LastErrorAt = null
                })
                .ConfigureAwait(false);

            if (!reconcileResult.IsSuccess)
            {
                LogStaleRemovingReconciliationFailed(
                    logger,
                    assignment.MapRotationServerAssignmentId,
                    reconcileResult.StatusCode);
                return false;
            }

            LogStaleRemovingAssignmentReconciled(logger, assignment.MapRotationServerAssignmentId);

            auditLogger.LogAudit(AuditEvent.SystemAction("MapRotationAssignmentReconciled", AuditAction.Update)
                .WithService("MapRotationCleanup")
                .WithTarget(assignment.MapRotationServerAssignmentId.ToString(), "MapRotationAssignment")
                .WithSource("MapRotationCleanup")
                .Build());

            return true;
        }
        catch (Exception ex)
        {
            LogStaleRemovingAssignmentReconciliationException(
                logger,
                assignment.MapRotationServerAssignmentId,
                ex);
            return false;
        }
    }

    private async Task<bool> DeleteRemovedAssignment(MapRotationServerAssignmentDto assignment, DateTime cutoff)
    {
        var retentionAnchor = assignment.UnassignedAt ?? assignment.UpdatedAt;
        if (retentionAnchor >= cutoff)
        {
            return false;
        }

        try
        {
            var deleteResult = await repositoryApiClient.MapRotations.V1
                .DeleteServerAssignment(assignment.MapRotationServerAssignmentId).ConfigureAwait(false);

            if (!deleteResult.IsSuccess)
            {
                LogRemovedAssignmentDeleteFailed(
                    logger,
                    assignment.MapRotationServerAssignmentId,
                    deleteResult.StatusCode);
                return false;
            }

            LogRemovedAssignmentDeleted(logger, assignment.MapRotationServerAssignmentId, assignment.UnassignedAt);

            auditLogger.LogAudit(AuditEvent.SystemAction("MapRotationAssignmentCleaned", AuditAction.Delete)
                .WithService("MapRotationCleanup")
                .WithTarget(assignment.MapRotationServerAssignmentId.ToString(), "MapRotationAssignment")
                .WithSource("MapRotationCleanup")
                .Build());

            return true;
        }
        catch (Exception ex)
        {
            LogAssignmentDeleteException(logger, assignment.MapRotationServerAssignmentId, ex);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting map rotation cleanup")]
    private static partial void LogCleanupStarting(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to retrieve server assignments for cleanup")]
    private static partial void LogServerAssignmentsRetrievalFailed(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipping stale removing reconciliation for assignment {AssignmentId} because operations could not be retrieved: {StatusCode}")]
    private static partial void LogStaleRemovingOperationsRetrievalFailed(
        ILogger logger,
        Guid assignmentId,
        System.Net.HttpStatusCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipping stale removing reconciliation for assignment {AssignmentId} because a recent in-progress Remove operation exists")]
    private static partial void LogRecentRemoveOperationFound(ILogger logger, Guid assignmentId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to reconcile stale removing assignment {AssignmentId}. API returned {StatusCode}")]
    private static partial void LogStaleRemovingReconciliationFailed(
        ILogger logger,
        Guid assignmentId,
        System.Net.HttpStatusCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Reconciled stale removing assignment {AssignmentId} to Removed")]
    private static partial void LogStaleRemovingAssignmentReconciled(ILogger logger, Guid assignmentId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to reconcile stale removing assignment {AssignmentId}")]
    private static partial void LogStaleRemovingAssignmentReconciliationException(
        ILogger logger,
        Guid assignmentId,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipping cleanup count/audit for assignment {AssignmentId} because delete failed: {StatusCode}")]
    private static partial void LogRemovedAssignmentDeleteFailed(
        ILogger logger,
        Guid assignmentId,
        System.Net.HttpStatusCode statusCode);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Deleted removed assignment {AssignmentId} (unassigned at {UnassignedAt})")]
    private static partial void LogRemovedAssignmentDeleted(ILogger logger, Guid assignmentId, DateTime? unassignedAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to delete assignment {AssignmentId}")]
    private static partial void LogAssignmentDeleteException(ILogger logger, Guid assignmentId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Map rotation cleanup completed, reconciled {ReconciledCount} stale removing assignments and deleted {DeletedCount} removed assignments")]
    private static partial void LogCleanupCompleted(ILogger logger, int reconciledCount, int deletedCount);
}
