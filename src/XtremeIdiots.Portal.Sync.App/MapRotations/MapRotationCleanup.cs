using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public class MapRotationCleanup(
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
        logger.LogInformation("Starting map rotation cleanup");

        var assignmentsResult = await repositoryApiClient.MapRotations.V1
            .GetServerAssignments(null, null, null, 0, 100).ConfigureAwait(false);

        if (!assignmentsResult.IsSuccess || assignmentsResult.Result?.Data?.Items is null)
        {
            logger.LogWarning("Failed to retrieve server assignments for cleanup");
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-48);
        var deletedCount = 0;

        foreach (var assignment in assignmentsResult.Result.Data.Items)
        {
            if (assignment.DeploymentState != DeploymentState.Removed)
                continue;

            if (assignment.UnassignedAt is null || assignment.UnassignedAt >= cutoff)
                continue;

            try
            {
                await repositoryApiClient.MapRotations.V1
                    .DeleteServerAssignment(assignment.MapRotationServerAssignmentId).ConfigureAwait(false);

                deletedCount++;
                logger.LogInformation(
                    "Deleted removed assignment {AssignmentId} (unassigned at {UnassignedAt})",
                    assignment.MapRotationServerAssignmentId, assignment.UnassignedAt);

                auditLogger.LogAudit(AuditEvent.SystemAction("MapRotationAssignmentCleaned", AuditAction.Delete)
                    .WithService("MapRotationCleanup")
                    .WithTarget(assignment.MapRotationServerAssignmentId.ToString(), "MapRotationAssignment")
                    .WithSource("MapRotationCleanup")
                    .Build());
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete assignment {AssignmentId}", assignment.MapRotationServerAssignmentId);
            }
        }

        logger.LogInformation("Map rotation cleanup completed, deleted {Count} assignments", deletedCount);
    }
}
