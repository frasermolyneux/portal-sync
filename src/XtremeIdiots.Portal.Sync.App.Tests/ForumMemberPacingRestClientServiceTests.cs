using System.Net;

using Moq;
using MX.Api.Client;
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
}
