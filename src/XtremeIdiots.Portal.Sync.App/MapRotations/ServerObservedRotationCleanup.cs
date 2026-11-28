using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public class ServerObservedRotationCleanup(
    ILogger<ServerObservedRotationCleanup> logger,
    IRepositoryApiClient repositoryApiClient,
    IJobTelemetry jobTelemetry)
{
    [Function(nameof(RunServerObservedRotationCleanupManual))]
    public Task RunServerObservedRotationCleanupManual(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
        CancellationToken cancellationToken = default) =>
        RunServerObservedRotationCleanup(null, cancellationToken);

    [Function(nameof(RunServerObservedRotationCleanup))]
    public Task RunServerObservedRotationCleanup([TimerTrigger("0 0 3 * * *")] TimerInfo? timer,
        CancellationToken cancellationToken = default) =>
        jobTelemetry.ExecuteAsync(nameof(RunServerObservedRotationCleanup), () => Cleanup(cancellationToken));

    private async Task Cleanup(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await repositoryApiClient.MapRotations.V1.CleanupUnassignedObservedRotations(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Errors?.Length > 0)
        {
            logger.LogError("Could not clean unassigned observed rotations: {StatusCode}; {Error}.",
                response.StatusCode, response.Result?.Errors?.FirstOrDefault()?.Message);
            throw new InvalidOperationException($"Could not clean unassigned observed rotations: {response.StatusCode}.");
        }
        logger.LogInformation("Unassigned observed rotation cleanup completed; Repository enforces retained assignment references.");
    }
}
