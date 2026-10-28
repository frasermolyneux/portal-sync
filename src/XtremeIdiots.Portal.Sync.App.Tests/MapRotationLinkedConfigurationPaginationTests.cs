using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationLinkedConfigurationPaginationTests
{
    private const int PageSize = 100;
    private static readonly DateTime Timestamp = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task EmptyOrShortFirstPageStopsPagination(int count)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var items = Enumerable.Range(0, count)
            .Select(_ => Assignment(Guid.NewGuid(), rotationId, "unrelated_variable"))
            .ToList();
        SetupPage(repository, serverId, 0, items);
        var activities = Create(repository);

        var result = await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(serverId, rotationId, selectedId, "sv_maprotation"));

        Assert.Empty(result);
        VerifyRequests(repository, serverId, 0);
    }

    [Theory]
    [InlineData("sv_maprotation", "scr_aacp_maps_1")]
    [InlineData("SCR_AACP_MAPS_1", "sv_maprotation")]
    public async Task FindsOnlyMatchingCounterpartAssignments(string variable, string counterpart)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var linkedId = Guid.NewGuid();
        var items = new[]
        {
            Assignment(selectedId, rotationId, counterpart),
            Assignment(linkedId, rotationId, counterpart.ToUpperInvariant()),
            Assignment(Guid.NewGuid(), rotationId, "unrelated_variable"),
            Assignment(Guid.NewGuid(), Guid.NewGuid(), counterpart),
            Assignment(Guid.NewGuid(), rotationId, counterpart, DeploymentState.Removed),
            Assignment(Guid.NewGuid(), rotationId, counterpart, DeploymentState.Removing)
        };
        SetupPage(repository, serverId, 0, items);
        var activities = Create(repository);

        var result = await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(serverId, rotationId, selectedId, variable));

        var target = Assert.Single(result);
        Assert.Equal(linkedId, target.AssignmentId);
        Assert.Equal("server.cfg", target.ConfigFilePath);
        Assert.Equal(counterpart.ToUpperInvariant(), target.ConfigVariableName);
        VerifyRequests(repository, serverId, 0);
    }

    [Fact]
    public async Task FullPagesFollowedByShortPageUseExpectedOffsetsAndQueryArguments()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var firstPage = Enumerable.Range(0, PageSize)
            .Select(_ => Assignment(Guid.NewGuid(), Guid.NewGuid(), "unrelated_variable"))
            .ToList();
        var secondPage = Enumerable.Range(0, PageSize)
            .Select(_ => Assignment(Guid.NewGuid(), Guid.NewGuid(), "unrelated_variable"))
            .ToList();
        var linkedId = Guid.NewGuid();
        var thirdPage = new[] { Assignment(linkedId, rotationId, "scr_aacp_maps_1") };
        SetupPage(repository, serverId, 0, firstPage);
        SetupPage(repository, serverId, 100, secondPage);
        SetupPage(repository, serverId, 200, thirdPage);
        var activities = Create(repository);

        var result = await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(serverId, rotationId, selectedId, "sv_maprotation"));

        Assert.Equal(linkedId, Assert.Single(result).AssignmentId);
        VerifyRequests(repository, serverId, 0, 100, 200);
    }

    [Fact]
    public async Task ExactlyFullFinalPageIsFollowedByEmptyPage()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        var fullPage = Enumerable.Range(0, PageSize)
            .Select(_ => Assignment(Guid.NewGuid(), rotationId, "sv_maprotation"))
            .ToList();
        SetupPage(repository, serverId, 0, fullPage);
        SetupPage(repository, serverId, 100, []);
        var activities = Create(repository);

        var result = await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(serverId, rotationId, selectedId, "scr_aacp_maps_1"));

        Assert.Equal(PageSize, result.Count);
        VerifyRequests(repository, serverId, 0, 100);
    }

    [Fact]
    public async Task UnsupportedVariableReturnsEmptyWithoutCallingApi()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var activities = Create(repository);

        var result = await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "scr_small_rotation"));

        Assert.Empty(result);
        Mock.Get(repository.Object.MapRotations.V1)
            .Verify(x => x.GetServerAssignments(null, It.IsAny<Guid>(), null, It.IsAny<int>(), PageSize,
                It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(0, true)]
    [InlineData(100, true)]
    public async Task UnsuccessfulOrMissingPageDataThrowsAndStopsPagination(int failureOffset, bool missingData)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var selectedId = Guid.NewGuid();
        if (failureOffset == 100)
        {
            SetupPage(repository, serverId, 0, [.. Enumerable.Range(0, PageSize)
                .Select(_ => Assignment(Guid.NewGuid(), rotationId, "unrelated_variable"))]);
        }
        SetupFailure(repository, serverId, failureOffset, missingData);
        var activities = Create(repository);
        var expectedStatus = missingData ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activities.GetLinkedConfigurationAssignments(
                new LinkedConfigurationInput(serverId, rotationId, selectedId, "sv_maprotation")));

        Assert.Equal($"Could not load linked configuration slots for server {serverId}: {expectedStatus}.",
            exception.Message);
        VerifyRequests(repository, serverId, failureOffset == 100 ? [0, 100] : [0]);
    }

    private static MapRotationActivities Create(Mock<IRepositoryApiClient> repository)
    {
        return new(Mock.Of<ILogger<MapRotationActivities>>(), repository.Object, Mock.Of<IServersApiClient>());
    }

    private static MapRotationServerAssignmentDto Assignment(
        Guid id,
        Guid rotationId,
        string variable,
        DeploymentState state = DeploymentState.Synced)
    {
        return new(id, rotationId, Guid.NewGuid(), state, ActivationState.Active, 1, 1, "server.cfg", variable,
            null, null, Timestamp, Timestamp, null);
    }

    private static void SetupPage(
        Mock<IRepositoryApiClient> repository,
        Guid serverId,
        int skip,
        IReadOnlyCollection<MapRotationServerAssignmentDto> items)
    {
        var api = Mock.Get(repository.Object.MapRotations.V1);
        _ = api.Setup(x => x.GetServerAssignments(null, serverId, null, skip, PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(
                    new CollectionModel<MapRotationServerAssignmentDto>([.. items]))));
    }

    private static void SetupFailure(
        Mock<IRepositoryApiClient> repository,
        Guid serverId,
        int skip,
        bool missingData)
    {
        var api = Mock.Get(repository.Object.MapRotations.V1);
        var result = missingData
            ? new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(
                    (CollectionModel<MapRotationServerAssignmentDto>?)null))
            : new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.ServiceUnavailable);
        _ = api.Setup(x => x.GetServerAssignments(null, serverId, null, skip, PageSize, It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private static void VerifyRequests(
        Mock<IRepositoryApiClient> repository,
        Guid serverId,
        params int[] offsets)
    {
        var api = Mock.Get(repository.Object.MapRotations.V1);
        foreach (var offset in offsets)
        {
            api.Verify(x => x.GetServerAssignments(null, serverId, null, offset, PageSize,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        api.Verify(x => x.GetServerAssignments(null, serverId, null, It.IsAny<int>(), PageSize,
            It.IsAny<CancellationToken>()), Times.Exactly(offsets.Length));
    }
}
