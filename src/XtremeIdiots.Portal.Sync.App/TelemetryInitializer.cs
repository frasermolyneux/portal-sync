using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace XtremeIdiots.Portal.Sync.App;

public static class TelemetryRegistration
{
    public static IServiceCollection AddSyncApplicationInsights(this IServiceCollection services)
    {
        services.AddApplicationInsightsTelemetryWorkerService(options => options.EnableAdaptiveSampling = false);
        services.ConfigureFunctionsApplicationInsights();
        // The Functions SDK also registers a role initializer; the canonical override must run last.
        services.AddSingleton<ITelemetryInitializer, TelemetryInitializer>();
        return services;
    }
}

public class TelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry)
    {
        telemetry.Context.Cloud.RoleName = "Sync FuncApp";
    }
}
