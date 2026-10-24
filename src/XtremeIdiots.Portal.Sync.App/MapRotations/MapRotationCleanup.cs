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

public class MapRotationCleanup(
    ILogger<MapRotationCleanup> logger,
    IRepositoryApiClient repositoryApiClient,
    IJobTelemetry jobTelemetry,
    IAuditLogger auditLogger)
{
    [Function(nameof(RunMapRotationCleanupManual))]
    public async Task RunMapRotationCleanupManual(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapRotationCleanup(null).ConfigureAwait(false);
    }

    [Function(nameof(RunMapRotationCleanup))]
    public async Task RunMapRotationCleanup([TimerTrigger("0 0 * * * *")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(nameof(RunMapRotationCleanup), ProcessCleanup).ConfigureAwait(false);
    }

    private async Task ProcessCleanup()
    {
        var assignments = new List<MapRotationServerAssignmentDto>();
        for (var skip = 0; ; skip += 100)
        {
            var response = await repositoryApiClient.MapRotations.V1
                .GetServerAssignments(null, null, null, skip, 100).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result?.Data?.Items is not { } page)
            {
                throw new InvalidOperationException($"Could not retrieve rotation assignments for cleanup: {response.StatusCode}.");
            }
            assignments.AddRange(page);
            if (page.Count() < 100)
            {
                break;
            }
        }

        var cutoff = DateTime.UtcNow.AddDays(-90);
        foreach (var assignment in assignments)
        {
            if (assignment.DeploymentState == DeploymentState.Removing && assignment.UpdatedAt < DateTime.UtcNow.AddMinutes(-15))
            {
                logger.LogWarning("Assignment {AssignmentId} is still Removing. Inspect/cancel its operation through the portal; elapsed time is not evidence of successful removal.",
                    assignment.MapRotationServerAssignmentId);
                continue;
            }
            if (assignment.DeploymentState != DeploymentState.Removed ||
                assignment.ActivationState != ActivationState.Inactive ||
                (assignment.UnassignedAt ?? assignment.UpdatedAt) >= cutoff)
            {
                continue;
            }

            var delete = await repositoryApiClient.MapRotations.V1
                .DeleteServerAssignment(assignment.MapRotationServerAssignmentId).ConfigureAwait(false);
            if (!delete.IsSuccess)
            {
                throw new InvalidOperationException($"Could not delete retained assignment {assignment.MapRotationServerAssignmentId}: {delete.StatusCode}.");
            }
            auditLogger.LogAudit(AuditEvent.SystemAction("MapRotationAssignmentCleaned", AuditAction.Delete)
                .WithService("MapRotationCleanup")
                .WithTarget(assignment.MapRotationServerAssignmentId.ToString(), "MapRotationAssignment")
                .WithSource("MapRotationCleanup").Build());
            logger.LogInformation("Deleted removed assignment {AssignmentId} after its 90-day evidence retention period.",
                assignment.MapRotationServerAssignmentId);
        }
    }
}
