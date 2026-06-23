using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace XtremeIdiots.Portal.Sync.App;

public class HealthCheck(HealthCheckService healthCheck)
{
    private readonly HealthCheckService healthCheck = healthCheck;

    [Function("HealthCheckReady")]
    public async Task<IActionResult> RunReady([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/ready")] HttpRequestData req,
        FunctionContext context)
    {
        var result = await healthCheck.CheckHealthAsync(context.CancellationToken).ConfigureAwait(false);
        var statusCode = result.Status == HealthStatus.Healthy
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;

        return new ObjectResult(new
        {
            status = result.Status.ToString(),
            checks = result.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
            }),
        })
        {
            StatusCode = statusCode,
        };
    }

    [Function("HealthCheckLive")]
    public IActionResult RunLive([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health/live")] HttpRequestData req)
    {
        return new OkObjectResult(new
        {
            status = HealthStatus.Healthy.ToString(),
        });
    }
}
