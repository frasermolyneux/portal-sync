using Microsoft.Extensions.DependencyInjection;

using MX.Api.Client.Extensions;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

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
/// These tests mirror the exact production registration shape (BaseUrl + Entra ID only, no
/// consumer-side caching) and resolve <see cref="IRepositoryApiClient"/> plus representative
/// typed sub-clients from a fully built <see cref="ServiceProvider"/>. Reintroducing a broken
/// <c>WithCaching</c> expression will cause these tests to fail before merge.
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
        // authentication only. Do not add consumer-side caching here — see class-level remarks.
        services.AddRepositoryApiClient(options => options
            .WithBaseUrl("https://repository.invalid")
            .WithEntraIdAuthentication("api://repository.invalid"));

        return services.BuildServiceProvider(validateScopes: true);
    }
}
