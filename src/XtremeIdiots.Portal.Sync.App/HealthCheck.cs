using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XtremeIdiots.Portal.Sync.App;

public class HealthCheck(HealthCheckService healthCheck)
{
    private readonly HealthCheckService healthCheck = healthCheck;
    [Function(nameof(HealthCheck))]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        FunctionContext context)
    {
        var healthStatus = await healthCheck.CheckHealthAsync();
        return new OkObjectResult(Enum.GetName(typeof(HealthStatus), healthStatus.Status));
    }
}
