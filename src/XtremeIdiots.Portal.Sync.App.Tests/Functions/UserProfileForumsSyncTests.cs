using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Jobs;
using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions.Models;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using System.Reflection;

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
