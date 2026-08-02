using System.Linq;
using System.Threading.Tasks;

using MX.Api.Abstractions;
using MX.Api.Client.Configuration;
using MX.Caching.Abstractions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Sync.App.Extensions;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class RepositoryApiCacheConfigurationTests
{
    [Fact]
    public void ConfigureCachePolicies_EnablesLibraryDefaults()
    {
        var options = BuildOptions();

        Assert.True(options.UseLibraryCacheDefaults);
    }

    [Fact]
    public void ConfigureCachePolicies_DisablesGetMapsList_ToProtectReadAfterWriteFlows()
    {
        var options = BuildOptions();

        var getMapsMethod = typeof(IMapsApi)
            .GetMethods()
            .Single(m => m.Name == nameof(IMapsApi.GetMaps));

        Assert.True(
            options.CachePolicyOperations.TryGetValue(getMapsMethod, out var operation),
            "Expected an explicit consumer cache policy for IMapsApi.GetMaps.");
        Assert.Equal(CachePolicyOperationKind.Disable, operation!.Kind);
    }

    [Fact]
    public void ConfigureCachePolicies_LeavesGetMapById_UnderLibraryDefaults()
    {
        var options = BuildOptions();

        var getMapByIdMethod = typeof(IMapsApi)
            .GetMethods()
            .Single(m => m.Name == nameof(IMapsApi.GetMap)
                && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(Guid));

        Assert.False(
            options.CachePolicyOperations.ContainsKey(getMapByIdMethod),
            "Read-only GetMap(Guid) should retain the library default 10-minute L1 policy.");
    }

    [Fact]
    public void ConfigureCachePolicies_LeavesGetGameServers_UnderLibraryDefaults()
    {
        var options = BuildOptions();

        var getGameServersMethod = typeof(IGameServersApi)
            .GetMethods()
            .Single(m => m.Name == nameof(IGameServersApi.GetGameServers));

        Assert.False(
            options.CachePolicyOperations.ContainsKey(getGameServersMethod),
            "GetGameServers is a read-only call in portal-sync and should retain the 60-second L1 default.");
    }

    private static ApiClientOptions BuildOptions()
    {
        var builder = new ApiClientOptionsBuilder();
        builder.WithBaseUrl("https://example.invalid");
        builder.WithCachePartition("portal-sync-tests");
        builder.WithCaching(RepositoryApiCacheConfiguration.ConfigureCachePolicies);
        return builder.Build();
    }
}
