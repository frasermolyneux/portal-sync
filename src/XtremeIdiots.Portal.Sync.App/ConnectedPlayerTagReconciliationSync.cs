using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;

using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App;

public class ConnectedPlayerTagReconciliationSync(
    IRepositoryApiClient repositoryApiClient,
    IJobTelemetry jobTelemetry)
{
    [Function(nameof(RunConnectedPlayerTagReconciliationSyncManual))]
    public async Task RunConnectedPlayerTagReconciliationSyncManual(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunConnectedPlayerTagReconciliationSync(null).ConfigureAwait(false);
    }

    [Function(nameof(RunConnectedPlayerTagReconciliationSync))]
    public async Task RunConnectedPlayerTagReconciliationSync([TimerTrigger("0 */5 * * * *")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(
            nameof(RunConnectedPlayerTagReconciliationSync),
            async () =>
            {
                await repositoryApiClient.DataMaintenance.V1.ReconcileConnectedPlayerTags().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }
}
