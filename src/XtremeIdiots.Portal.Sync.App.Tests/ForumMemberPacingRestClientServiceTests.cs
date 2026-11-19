using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;
using MX.Api.Client;
using MX.Api.Client.Configuration;
using MX.Api.Client.Extensions;
using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Client;
using RestSharp;
using XtremeIdiots.Portal.Sync.App;

namespace XtremeIdiots.Portal.Sync.App.Tests;

public class ForumMemberPacingRestClientServiceTests
{
    [Fact]
    public async Task ExecuteAsync_RateLimitedMemberLookupIsDeferredBeforeClientRetryPolicy()
    {
        var response = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
        var inner = new Mock<IRestClientService>();
        inner.Setup(client => client.ExecuteAsync(
                "https://forums.example",
                It.IsAny<RestRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var sut = new ForumMemberPacingRestClientService(
            inner.Object,
            new ForumMemberRequestPacer(),
            "https://forums.example");

        await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(() => sut.ExecuteAsync(
            "https://forums.example",
            new RestRequest("api/core/members/42", Method.Get),
            CancellationToken.None));

        inner.Verify(client => client.ExecuteAsync(
            "https://forums.example",
            It.Is<RestRequest>(request => request.Resource == "api/core/members/42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NonMemberRequestIsPassedThroughWithoutDeferral()
    {
        var response = new RestResponse { StatusCode = HttpStatusCode.TooManyRequests };
        var inner = new Mock<IRestClientService>();
        inner.Setup(client => client.ExecuteAsync(
                "https://forums.example",
                It.IsAny<RestRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        var sut = new ForumMemberPacingRestClientService(
            inner.Object,
            new ForumMemberRequestPacer(),
            "https://forums.example");

        var result = await sut.ExecuteAsync(
            "https://forums.example",
            new RestRequest("api/core/hello", Method.Get),
            CancellationToken.None);

        Assert.Same(response, result);
    }

    [Fact]
    public async Task GetMember_RateLimitedResponseIsInterceptedOnceThroughRegisteredClientPipeline()
    {
        const string forumsBaseUrl = "https://forums.example";
        var response = new RestResponse
        {
            StatusCode = HttpStatusCode.TooManyRequests,
            Content = "Rate limited",
            Headers = [new HeaderParameter("Retry-After", "12", false)]
        };
        var inner = new Mock<IRestClientService>();
        inner.Setup(client => client.ExecuteAsync(
                forumsBaseUrl,
                It.Is<RestRequest>(request => request.Resource == "api/core/members/42" && request.Method == Method.Get),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInvisionApiClient(options => options
            .WithBaseUrl(forumsBaseUrl)
            .WithApiKeyAuthentication("test-api-key", "key", ApiKeyLocation.QueryParameter)
            .WithCachePartition("forum-pacing-tests")
            .WithCaching(cache => cache.UseLibraryDefaults()));
        services.AddSingleton<ForumMemberRequestPacer>();
        services.AddScoped<IRestClientService>(provider => new ForumMemberPacingRestClientService(
            inner.Object,
            provider.GetRequiredService<ForumMemberRequestPacer>(),
            forumsBaseUrl));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<IInvisionApiClient>();

        var exception = await Assert.ThrowsAsync<ForumMemberRequestDeferredException>(
            () => apiClient.Core.GetMember("42", CancellationToken.None));

        Assert.True(exception.DeferredUntilUtc > DateTimeOffset.UtcNow);
        inner.Verify(client => client.ExecuteAsync(
            forumsBaseUrl,
            It.Is<RestRequest>(request => request.Resource == "api/core/members/42"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
