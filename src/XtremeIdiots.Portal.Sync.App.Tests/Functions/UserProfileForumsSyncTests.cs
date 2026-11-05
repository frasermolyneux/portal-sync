using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions.Models;
using Newtonsoft.Json;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using System.Reflection;
using XtremeIdiots.Portal.Sync.App.Observability;

namespace XtremeIdiots.Portal.Sync.App.Tests.Functions;

public class UserProfileForumsSyncTests
{
    private const string ClanMemberClaimType = "ClanMember";
    private const string RegisteredUserClaimType = "RegisteredUser";

    private readonly Mock<ILogger<UserProfileForumsSync>> _loggerMock = new();
    private readonly Mock<IRepositoryApiClient> _repositoryApiClientMock = new(MockBehavior.Loose) { DefaultValue = DefaultValue.Mock };
    private readonly Mock<IInvisionApiClient> _invisionApiClientMock = new();
    private readonly Mock<IJobTelemetry> _jobTelemetryMock = new();
    private readonly Mock<IAuditLogger> _auditLoggerMock = new();

    public UserProfileForumsSyncTests()
    {
        _jobTelemetryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<string>(), It.IsAny<Func<Task>>(), It.IsAny<Dictionary<string, string>?>()))
            .Returns<string, Func<Task>, Dictionary<string, string>?>((_, action, _) => action());
    }

    private UserProfileForumsSync CreateSut() => new(
        _loggerMock.Object,
        _repositoryApiClientMock.Object,
        _invisionApiClientMock.Object,
        _jobTelemetryMock.Object,
        _auditLoggerMock.Object);

    [Fact]
    public async Task RunUserProfileForumsSync_LogsStructuredProfileAndUnexpectedFailure()
    {
        var profiles = new[] { (Name: "Alice", ForumId: "42"), (Name: "Bob", ForumId: "broken"), (Name: "No forum", ForumId: (string?)null) }
            .Select(profile => JsonConvert.DeserializeObject<UserProfileDto>(JsonConvert.SerializeObject(new
            {
                UserProfileId = Guid.NewGuid(),
                DisplayName = profile.Name,
                XtremeIdiotsForumId = profile.ForumId,
                UserProfileClaims = Array.Empty<object>()
            }))!).ToArray();
        var users = Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1);
        users.SetupSequence(client => client.GetUserProfiles(It.IsAny<string?>(), It.IsAny<UserProfileFilter?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<UserProfilesOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>(profiles))))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>([]))));
        _invisionApiClientMock.Setup(client => client.Core.GetMember("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MemberDto>(System.Net.HttpStatusCode.OK,
                new ApiResponse<MemberDto>(CreateMemberDto(42, CreateGroupDto(82, "Members")))));
        var exception = new InvalidOperationException("Unexpected forum failure");
        _invisionApiClientMock.Setup(client => client.Core.GetMember("broken", It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
        users.Setup(client => client.UpdateUserProfile(It.IsAny<EditUserProfileDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));
        users.Setup(client => client.SetUserProfileClaims(It.IsAny<Guid>(), It.IsAny<List<CreateUserProfileClaimDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var failure = await Assert.ThrowsAsync<PartialJobFailureException>(() => CreateSut().RunUserProfileForumsSync(null));

        Assert.Equal(2, failure.Expected);
        Assert.Equal(1, failure.Failed);
        var logs = _loggerMock.Invocations.Where(invocation => invocation.Method.Name == nameof(ILogger.Log)).ToArray();
        Assert.Equal(4, logs.Length);
        AssertProfileLog(logs[0].Arguments, "Alice", "42");
        AssertProfileLog(logs[1].Arguments, "Bob", "broken");
        Assert.Equal(LogLevel.Error, logs[2].Arguments[0]);
        var errorState = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(logs[2].Arguments[2]);
        Assert.Equal("Failed to sync forum profile for {ForumId}", errorState.Single(value => value.Key == "{OriginalFormat}").Value);
        Assert.Equal("broken", errorState.Single(value => value.Key == "ForumId").Value);
        Assert.Equal("Failed to sync forum profile for broken", logs[2].Arguments[2]?.ToString());
        Assert.Same(exception, logs[2].Arguments[3]);
        AssertProfileLog(logs[3].Arguments, "No forum", string.Empty);
    }

    private static void AssertProfileLog(IReadOnlyList<object?> arguments, string displayName, string forumId)
    {
        Assert.Equal(LogLevel.Information, arguments[0]);
        var state = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(arguments[2]);
        Assert.Equal("UserProfileSync for '{DisplayName}' with XtremeIdiots ID '{ForumId}'", state.Single(value => value.Key == "{OriginalFormat}").Value);
        Assert.Equal(displayName, state.Single(value => value.Key == "DisplayName").Value);
        Assert.Equal(forumId, state.Single(value => value.Key == "ForumId").Value);
        Assert.Equal($"UserProfileSync for '{displayName}' with XtremeIdiots ID '{forumId}'", arguments[2]?.ToString());
        Assert.Null(arguments[3]);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    public async Task FailedForumMemberDoesNotPreventLaterProfilesFromSynchronizing(System.Net.HttpStatusCode statusCode)
    {
        string[] forumIds = ["missing", "valid"];
        var profiles = forumIds.Select(forumId =>
            JsonConvert.DeserializeObject<UserProfileDto>(JsonConvert.SerializeObject(new
            {
                UserProfileId = Guid.NewGuid(),
                DisplayName = forumId,
                XtremeIdiotsForumId = forumId,
                UserProfileClaims = Array.Empty<object>()
            })) ?? throw new InvalidOperationException("Invalid profile fixture.")).ToArray();
        var users = Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1);
        users.SetupSequence(client => client.GetUserProfiles(It.IsAny<string?>(), It.IsAny<UserProfileFilter?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<UserProfilesOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>(profiles))))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>([]))));
        _invisionApiClientMock.Setup(client => client.Core.GetMember("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MemberDto>(statusCode));
        _invisionApiClientMock.Setup(client => client.Core.GetMember("valid", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MemberDto>(System.Net.HttpStatusCode.OK,
                new ApiResponse<MemberDto>(CreateMemberDto(123, CreateGroupDto(82, "Members")))));
        users.Setup(client => client.UpdateUserProfile(It.IsAny<EditUserProfileDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));
        users.Setup(client => client.SetUserProfileClaims(It.IsAny<Guid>(), It.IsAny<List<CreateUserProfileClaimDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult(System.Net.HttpStatusCode.OK));

        var error = await Assert.ThrowsAsync<PartialJobFailureException>(() => CreateSut().RunUserProfileForumsSync(null));

        Assert.Equal(2, error.Expected);
        Assert.Equal(1, error.Failed);
        users.Verify(client => client.SetUserProfileClaims(profiles[1].UserProfileId,
            It.IsAny<List<CreateUserProfileClaimDto>>(), It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(client => client.SetUserProfileClaims(profiles[0].UserProfileId,
            It.IsAny<List<CreateUserProfileClaimDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(client => client.ArchiveUserProfile(
            It.IsAny<ArchiveUserProfileDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ArchivedProfile_IsNotSentToForumsOrMutated()
    {
        var profile = JsonConvert.DeserializeObject<UserProfileDto>(JsonConvert.SerializeObject(new
        {
            UserProfileId = Guid.NewGuid(),
            XtremeIdiotsForumId = "42",
            ArchivedAtUtc = DateTime.UtcNow,
            UserProfileClaims = Array.Empty<object>()
        }))!;
        var users = Mock.Get(_repositoryApiClientMock.Object.UserProfiles.V1);
        users.SetupSequence(client => client.GetUserProfiles(It.IsAny<string?>(), It.IsAny<UserProfileFilter?>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<UserProfilesOrder?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>([profile]))))
            .ReturnsAsync(new ApiResult<CollectionModel<UserProfileDto>>(System.Net.HttpStatusCode.OK,
                new ApiResponse<CollectionModel<UserProfileDto>>(new CollectionModel<UserProfileDto>([]))));

        await CreateSut().RunUserProfileForumsSync(null);

        _invisionApiClientMock.VerifyNoOtherCalls();
        users.Verify(client => client.UpdateUserProfile(
            It.IsAny<EditUserProfileDto>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(client => client.SetUserProfileClaims(It.IsAny<Guid>(),
            It.IsAny<List<CreateUserProfileClaimDto>>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(client => client.ArchiveUserProfile(
            It.IsAny<ArchiveUserProfileDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

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
    public async Task RunUserProfileForumsSync_WhenApiReturnsFailure_DoesNotReportSuccessfulCompletion()
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

        await Assert.ThrowsAsync<HttpRequestException>(() => sut.RunUserProfileForumsSync(null));
    }

    [Fact]
    public void GetClaimsForMember_WhenMemberInClanGroupAndNoHigherRole_AddsClanMemberClaim()
    {
        var userProfileId = Guid.NewGuid();
        var member = CreateMemberDto(123, CreateGroupDto(82, "Members"));

        var claims = InvokeGetClaimsForMember(userProfileId, member);

        Assert.Contains(claims, c => c.ClaimType == ClanMemberClaimType && c.ClaimValue == GameType.Unknown.ToString());
        Assert.DoesNotContain(claims, c => c.ClaimType == RegisteredUserClaimType);
    }

    [Fact]
    public void GetClaimsForMember_WhenNoHigherRoleAndNotClanMember_AddsRegisteredUserClaim()
    {
        var userProfileId = Guid.NewGuid();
        var member = CreateMemberDto(456, CreateGroupDto(10, "Members"));

        var claims = InvokeGetClaimsForMember(userProfileId, member);

        Assert.Contains(claims, c => c.ClaimType == RegisteredUserClaimType && c.ClaimValue == GameType.Unknown.ToString());
        Assert.DoesNotContain(claims, c => c.ClaimType == ClanMemberClaimType);
    }

    [Fact]
    public void GetClaimsForMember_WhenClanMemberIsInSecondaryGroups_AddsClanMemberClaim()
    {
        var userProfileId = Guid.NewGuid();
        var member = CreateMemberDto(
            600,
            CreateGroupDto(10, "Members"),
            [CreateGroupDto(82, "Members")]);

        var claims = InvokeGetClaimsForMember(userProfileId, member);

        Assert.Contains(claims, c => c.ClaimType == ClanMemberClaimType && c.ClaimValue == GameType.Unknown.ToString());
        Assert.DoesNotContain(claims, c => c.ClaimType == RegisteredUserClaimType);
    }

    [Fact]
    public void GetClaimsForMember_WhenHigherRoleExists_DoesNotAddClanMemberOrRegisteredUserClaims()
    {
        var userProfileId = Guid.NewGuid();
        var member = CreateMemberDto(
            789,
            CreateGroupDto(20, "COD4 Admin"),
            [CreateGroupDto(82, "Members")]);

        var claims = InvokeGetClaimsForMember(userProfileId, member);

        Assert.Contains(claims, c => c.ClaimType == UserProfileClaimType.GameAdmin);
        Assert.DoesNotContain(claims, c => c.ClaimType == ClanMemberClaimType);
        Assert.DoesNotContain(claims, c => c.ClaimType == RegisteredUserClaimType);
    }

    [Fact]
    public void GetClaimsForMember_WhenMemberInWebmasterGroup_AddsWebmasterClaimAndNoLowerRoleFallback()
    {
        var userProfileId = Guid.NewGuid();
        var member = CreateMemberDto(
            321,
            CreateGroupDto(117, "Webmaster"),
            [CreateGroupDto(82, "Members")]);

        var claims = InvokeGetClaimsForMember(userProfileId, member);

        Assert.Contains(claims, c => c.ClaimType == UserProfileClaimType.Webmaster && c.ClaimValue == GameType.Unknown.ToString());
        Assert.DoesNotContain(claims, c => c.ClaimType == ClanMemberClaimType);
        Assert.DoesNotContain(claims, c => c.ClaimType == RegisteredUserClaimType);
    }

    private static List<CreateUserProfileClaimDto> InvokeGetClaimsForMember(Guid userProfileId, MemberDto member)
    {
        var methodInfo = typeof(UserProfileForumsSync).GetMethod("GetClaimsForMember", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(methodInfo);

        var claims = methodInfo.Invoke(null, [userProfileId, member]) as List<CreateUserProfileClaimDto>;
        Assert.NotNull(claims);

        return claims;
    }

    private static MemberDto CreateMemberDto(long memberId, GroupDto primaryGroup, List<GroupDto>? secondaryGroups = null)
    {
        var member = new MemberDto();
        SetProperty(member, nameof(MemberDto.Id), memberId);
        SetProperty(member, nameof(MemberDto.PrimaryGroup), primaryGroup);
        SetProperty(member, nameof(MemberDto.SecondaryGroups), secondaryGroups?.ToArray() ?? []);

        return member;
    }

    private static GroupDto CreateGroupDto(long groupId, string groupName)
    {
        var group = new GroupDto();
        SetProperty(group, nameof(GroupDto.Id), groupId);
        SetProperty(group, nameof(GroupDto.Name), groupName);

        return group;
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);

        property.SetValue(target, value);
    }
}
