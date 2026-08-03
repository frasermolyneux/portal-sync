using Microsoft.Extensions.DependencyInjection;

using MX.Api.Client.Configuration;
using MX.Api.Client.Extensions;

using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions.Interfaces;
using MX.InvisionCommunity.Api.Client;

using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.Tests;

/// <summary>
/// Guards the Invision and Servers API client DI registrations used by <c>Program.cs</c> against
/// the same class of startup-crash regression that took portal-sync down under
/// <c>Repository.Api.Client.V1 4.2.21</c> (PR #832 → hotfix PR #833 → rolled forward under PR #834
/// on <c>MX.Api.Client 2.3.77</c>'s <see cref="SharedCacheConfiguration"/>).
///
/// The exact failure mode was an <see cref="ArgumentException"/> — "The expression must invoke a
/// method declared by ... or one of its inherited interfaces" — raised inside the per-sub-client
/// options callback executed by <c>Add*ApiClient</c> during DI configuration. That crashed the
/// Functions worker before host startup. This test mirrors the Invision + Servers client
/// registrations from <c>Program.cs</c> exactly, builds a fully validated <see cref="ServiceProvider"/>,
/// and resolves every typed sub-client consumed by portal-sync. Any future regression in either
/// library's DI options callback — including a reintroduction of the cross-sub-API expression
/// fan-out — will fail these tests before merge.
///
/// The Invision registration mirrors the caching decision documented in the PR: only
/// <see cref="ICoreApi.GetMember"/> and <see cref="IDownloadsApi.GetDownloadFile"/> are consumed by
/// portal-sync (via <c>UserProfileForumsSync</c> and <c>DemoManager</c> respectively), both are
/// pure reads whose writes land against a different backing store (the Repository API for user
/// profile updates; no writes at all for downloads), and <see cref="IForumsApi"/> — which handles
/// the sync write path via <c>PostTopic</c>/<c>UpdateTopic</c> — is uncached by
/// <c>UseLibraryDefaults()</c>. Turning on library defaults is therefore safe. The Servers client
/// ships no cache defaults; the bump to <c>4.1.14</c> is purely for version currency and crash
/// safety.
/// </summary>
public class ExternalApiClientRegistrationTests
{
    [Fact]
    public void AddInvisionApiClient_ProductionShape_DoesNotThrowDuringRegistration()
    {
        // The PR #832 regression exception was raised inside the options-configuration callback
        // that Add*ApiClient invokes per sub-client. Registration + provider build must not throw.
        var exception = Record.Exception(BuildInvisionServiceProvider);

        Assert.Null(exception);
    }

    [Fact]
    public void AddInvisionApiClient_ProductionShape_ResolvesInvisionApiClient()
    {
        using var provider = BuildInvisionServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IInvisionApiClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(typeof(ICoreApi))]
    [InlineData(typeof(IDownloadsApi))]
    [InlineData(typeof(IForumsApi))]
    public void AddInvisionApiClient_ProductionShape_ResolvesTypedSubClient(Type subClientType)
    {
        using var provider = BuildInvisionServiceProvider();
        using var scope = provider.CreateScope();

        var subClient = scope.ServiceProvider.GetRequiredService(subClientType);

        Assert.NotNull(subClient);
    }

    [Fact]
    public void AddServersApiClient_ProductionShape_DoesNotThrowDuringRegistration()
    {
        var exception = Record.Exception(BuildServersServiceProvider);

        Assert.Null(exception);
    }

    [Fact]
    public void AddServersApiClient_ProductionShape_ResolvesServersApiClient()
    {
        using var provider = BuildServersServiceProvider();
        using var scope = provider.CreateScope();

        var client = scope.ServiceProvider.GetRequiredService<IServersApiClient>();

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(typeof(IMapsApi))]
    [InlineData(typeof(IConfigApi))]
    [InlineData(typeof(ICod2RconApi))]
    [InlineData(typeof(ICod4RconApi))]
    [InlineData(typeof(ICoD4xRconApi))]
    [InlineData(typeof(ICod5RconApi))]
    public void AddServersApiClient_ProductionShape_ResolvesTypedSubClient(Type subClientType)
    {
        using var provider = BuildServersServiceProvider();
        using var scope = provider.CreateScope();

        var subClient = scope.ServiceProvider.GetRequiredService(subClientType);

        Assert.NotNull(subClient);
    }

    private static ServiceProvider BuildInvisionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mirror Program.cs exactly: BaseUrl + API key authentication + cache partition
        // + UseLibraryDefaults(). Only GetCoreHello / GetMember / GetDownloadFile are cached by
        // MX.InvisionCommunity.Api.Client 1.0.63 defaults; IForumsApi is entirely uncached.
        services.AddInvisionApiClient(options => options
            .WithBaseUrl("https://forums.invalid")
            .WithApiKeyAuthentication("test-api-key", "key", ApiKeyLocation.QueryParameter)
            .WithCachePartition("portal-sync")
            .WithCaching(cache => cache.UseLibraryDefaults()));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static ServiceProvider BuildServersServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Mirror Program.cs exactly: BaseUrl + Entra ID authentication. The Servers client ships
        // no cache defaults, so no WithCaching() call is made — the bump to 4.1.14 gives us the
        // crash-safe per-sub-client scoping without changing runtime behaviour.
        services.AddServersApiClient(options => options
            .WithBaseUrl("https://servers.invalid")
            .WithEntraIdAuthentication("api://servers.invalid"));

        return services.BuildServiceProvider(validateScopes: true);
    }
}
