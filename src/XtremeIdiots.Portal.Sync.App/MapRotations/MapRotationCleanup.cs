using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public class MapRotationCleanup(
    ILogger<MapRotationCleanup> logger,
    IRepositoryApiClient repositoryApiClient,
    TelemetryClient telemetryClient)
{
    private readonly ILogger<MapRotationCleanup> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly TelemetryClient telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));

    [Function(nameof(RunMapRotationCleanupManual))]
    public async Task RunMapRotationCleanupManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunMapRotationCleanup(null).ConfigureAwait(false);
    }

    [Function(nameof(RunMapRotationCleanup))]
    public async Task RunMapRotationCleanup([TimerTrigger("0 0 * * * *")] TimerInfo? myTimer)
    {
        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            telemetryClient,
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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to delete assignment {AssignmentId}", assignment.MapRotationServerAssignmentId);
            }
        }

        logger.LogInformation("Map rotation cleanup completed, deleted {Count} assignments", deletedCount);
    }
}
