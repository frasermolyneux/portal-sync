using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class UserProfileForumsSyncTests
{
    private readonly Mock<ILogger<UserProfileForumsSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IInvisionApiClient> _invisionApiClientMock = new();
    private readonly TelemetryClient _telemetryClient = new(new TelemetryConfiguration());

    private UserProfileForumsSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _invisionApiClientMock.Object,
        _telemetryClient);

    [Fact]
    public void Constructor_WithValidDependencies_ShouldNotThrow()
    {
        var sut = CreateSut();
        Assert.NotNull(sut);
    }

    [Fact]
    public async Task RunUserProfileForumsSync_WhenApiReturnsNoProfiles_CompletesWithoutError()
    {
        var emptyCollection = new CollectionModel<UserProfileDto>(new List<UserProfileDto>());
        var response = new ApiResponse<CollectionModel<UserProfileDto>>(emptyCollection);
        var apiResult = new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK, response);

        Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1)
            .Setup(x => x.GetUserProfiles(
                It.IsAny<string?>(),
                It.IsAny<UserProfileFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<UserProfilesOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(apiResult);

        var sut = CreateSut();
        await sut.RunUserProfileForumsSync(null);

        Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1)
            .Verify(x => x.GetUserProfiles(It.IsAny<string?>(), It.IsAny<UserProfileFilter?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<UserProfilesOrder?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunUserProfileForumsSync_WhenApiReturnsFailure_CompletesWithWarning()
    {
        var failResult = new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.InternalServerError);

        Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1)
            .Setup(x => x.GetUserProfiles(
                It.IsAny<string?>(),
                It.IsAny<UserProfileFilter?>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<UserProfilesOrder?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);

        var sut = CreateSut();

        // Should not throw - logs warning and breaks out of loop
        await sut.RunUserProfileForumsSync(null);
    }
}
