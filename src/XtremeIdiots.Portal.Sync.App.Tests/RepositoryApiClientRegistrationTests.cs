using Microsoft.Extensions.DependencyInjection;

using MX.Api.Client.Extensions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Extensions;

namespace XtremeIdiots.Portal.Sync.App.Tests;

/// <summary>
/// Guards the Repository API client DI registration used by <c>Program.cs</c> against regressions
/// that crash the Functions host at startup.
///
/// Production incident (PR #832) was caused by a consumer-side cache-policy expression referencing
/// one typed sub-interface (<see cref="IMapsApi"/>) but applied across every typed sub-client by
/// <c>AddRepositoryApiClient</c>. During DI configuration the expression tree was evaluated while
/// building options for <see cref="IAdminActionsApi"/>, throwing
/// <see cref="ArgumentException"/>: "The expression must invoke a method declared by
/// XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1.IAdminActionsApi or one of its
/// inherited interfaces." The Functions worker exited before host startup.
///
/// PR #833 hotfixed by removing all consumer caching. Repository client 4.2.22 + MX.Api.Client
/// 2.3.77 fix the root cause by scoping every cache-policy expression to its matching typed
/// sub-API via <c>SharedCacheConfiguration</c>. These tests mirror the exact production
/// registration shape from <c>Program.cs</c> — including the re-enabled
/// <see cref="RepositoryApiCacheConfiguration"/> cache-policy chain — and resolve
/// <see cref="IRepositoryApiClient"/> plus every typed sub-client portal-sync consumes from a
/// fully built <see cref="ServiceProvider"/>. Any future regression that reintroduces a
/// cross-sub-API expression, or otherwise breaks the DI options callback, will fail these tests
/// before merge.
/// </summary>
public class RepositoryApiClientRegistrationTests
{
    [Fact]
    public void AddRepositoryApiClient_ProductionShape_DoesNotThrowDuringRegistration()
    {
        // Registration itself must not throw: the PR #832 regression exception was raised inside
        // the options-configuration callback that AddRepositoryApiClient invokes per sub-client.
        var exception = Record.Exception(BuildProductionServiceProvider);

        Assert.Null(exception);
    }

    [Fact]
    public void AddRepositoryApiClient_ProductionShape_ResolvesRepositoryApiClient()
    {
        using var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IRepositoryApiClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(typeof(IAdminActionsApi))]
    [InlineData(typeof(IMapsApi))]
    [InlineData(typeof(IGameServersApi))]
    [InlineData(typeof(IUserProfileApi))]
    [InlineData(typeof(IBanFileMonitorsApi))]
    [InlineData(typeof(IMapRotationsApi))]
    [InlineData(typeof(ICentralBanFileStatusApi))]
    [InlineData(typeof(IDataMaintenanceApi))]
    public void AddRepositoryApiClient_ProductionShape_ResolvesTypedSubClient(Type subClientType)
    {
        using var provider = BuildProductionServiceProvider();
        using var scope = provider.CreateScope();

        var subClient = scope.ServiceProvider.GetRequiredService(subClientType);

        Assert.NotNull(subClient);
    }

    private static ServiceProvider BuildProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mirror the production registration shape from Program.cs exactly: BaseUrl + Entra ID
        // authentication + the re-enabled consumer cache-policy chain from
        // RepositoryApiCacheConfiguration. See class-level remarks for why the full production
        // shape (including caching) is under test here.
        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://repository.invalid")
            .WithEntraIdAuthentication("api://repository.invalid")
            .WithCaching(true)
            .WithCachePartition("portal-sync")
            .WithCaching(RepositoryApiCacheConfiguration.ConfigureCachePolicies));

        return services.BuildServiceProvider(validateScopes: true);
    }
}
