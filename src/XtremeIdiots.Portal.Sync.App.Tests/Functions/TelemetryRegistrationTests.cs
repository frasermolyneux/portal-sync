using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class TelemetryRegistrationTests
{
    [Fact]
    public async Task CanonicalRoleOverridesTheFunctionsSdkInitializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["WEBSITE_SITE_NAME"] = "fn-portal-sync-func-prd-example"
            }).Build());
        services.AddSyncApplicationInsights();
        await using var provider = services.BuildServiceProvider();
        var configuration = provider.GetRequiredService<TelemetryConfiguration>();
        var telemetry = new EventTelemetry("ProcessHealthSnapshot");
        foreach (var initializer in configuration.TelemetryInitializers)
        {
            initializer.Initialize(telemetry);
        }
        Assert.Equal("Sync FuncApp", telemetry.Context.Cloud.RoleName);
        Assert.IsType<TelemetryInitializer>(configuration.TelemetryInitializers.Last());
    }
}
