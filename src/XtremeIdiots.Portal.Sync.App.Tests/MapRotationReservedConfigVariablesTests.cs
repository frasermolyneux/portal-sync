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
public class MapRotationReservedConfigVariablesTests
{
    private const int PageSize = 100;
    private static readonly DateTime Timestamp = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task EmptyOrShortPageStopsPagination(int count)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var assignments = Enumerable.Range(0, count)
            .Select(index => Assignment(Guid.NewGuid(), $"reserved_{index}"))
            .ToList();
        SetupPage(repository, serverId, 0, assignments);
        var activities = Create(repository);

        var result = await activities.GetReservedConfigVariables(
            new ConfigSlotInput(serverId, Guid.NewGuid(), "selected_variable"));

        Assert.Equal(count, result.Count);
        Assert.All(Enumerable.Range(0, count), index => Assert.Contains($"reserved_{index}", result));
        VerifyRequests(repository, serverId, 0);
    }

    [Fact]
    public async Task ExactlyFullFinalPageIsFollowedByEmptyPage()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var fullPage = Enumerable.Range(0, PageSize)
            .Select(index => Assignment(Guid.NewGuid(), $"reserved_{index}"))
            .ToList();
        SetupPage(repository, serverId, 0, fullPage);
        SetupPage(repository, serverId, PageSize, []);
        var activities = Create(repository);

        var result = await activities.GetReservedConfigVariables(
            new ConfigSlotInput(serverId, Guid.NewGuid(), "selected_variable"));

        Assert.Equal(PageSize, result.Count);
        Assert.Contains("reserved_0", result);
        VerifyRequests(repository, serverId, 0, PageSize);
    }

    [Fact]
    public async Task MultipleFullPagesFollowedByShortPageUseExpectedOffsets()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var firstPage = Enumerable.Range(0, PageSize)
            .Select(index => Assignment(Guid.NewGuid(), $"first_{index}"))
            .ToList();
        var secondPage = Enumerable.Range(0, PageSize)
            .Select(index => Assignment(Guid.NewGuid(), $"second_{index}"))
            .ToList();
        var lastPage = new[] { Assignment(Guid.NewGuid(), "last_0"), Assignment(Guid.NewGuid(), "last_1") };
        SetupPage(repository, serverId, 0, firstPage);
        SetupPage(repository, serverId, PageSize, secondPage);
        SetupPage(repository, serverId, PageSize * 2, lastPage);
        var activities = Create(repository);

        var result = await activities.GetReservedConfigVariables(
            new ConfigSlotInput(serverId, Guid.NewGuid(), "selected_variable"));

        Assert.Equal((PageSize * 2) + lastPage.Length, result.Count);
        Assert.Contains("first_0", result);
        Assert.Contains("second_0", result);
        Assert.Contains("last_0", result);
        VerifyRequests(repository, serverId, 0, PageSize, PageSize * 2);
    }

    [Fact]
    public async Task FullPageOfFilteredAssignmentsStillContinues()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var selectedAssignmentId = Guid.NewGuid();
        var filteredPage = Enumerable.Range(0, PageSize)
            .Select(_ => Assignment(selectedAssignmentId, "selected_variable"))
            .ToList();
        SetupPage(repository, serverId, 0, filteredPage);
        SetupPage(repository, serverId, PageSize, [Assignment(Guid.NewGuid(), "reserved")]);
        var activities = Create(repository);

        var result = await activities.GetReservedConfigVariables(
            new ConfigSlotInput(serverId, selectedAssignmentId, "selected_variable"));

        Assert.Equal(["reserved"], result);
        VerifyRequests(repository, serverId, 0, PageSize);
    }

    [Fact]
    public async Task ExcludesOwnedAndInvalidVariablesAndDeduplicatesCaseInsensitively()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        var selectedAssignmentId = Guid.NewGuid();
        SetupPage(repository, serverId, 0,
        [
            Assignment(selectedAssignmentId, "owned_by_assignment"),
            Assignment(Guid.NewGuid(), "SELECTED_VARIABLE"),
            Assignment(Guid.NewGuid(), "removed", DeploymentState.Removed),
            Assignment(Guid.NewGuid(), null),
            Assignment(Guid.NewGuid(), "  "),
            Assignment(Guid.NewGuid(), "Reserved"),
            Assignment(Guid.NewGuid(), "RESERVED"),
            Assignment(Guid.NewGuid(), "retained_removing", DeploymentState.Removing)
        ]);
        var activities = Create(repository);

        var result = await activities.GetReservedConfigVariables(
            new ConfigSlotInput(serverId, selectedAssignmentId, "selected_variable"));

        var expected = new HashSet<string>(["reserved", "retained_removing"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.Count, result.Count);
        Assert.True(expected.SetEquals(result));
        VerifyRequests(repository, serverId, 0);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(PageSize, false)]
    [InlineData(PageSize, true)]
    public async Task UnsuccessfulOrMissingDataThrowsExistingException(int failureOffset, bool missingData)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var serverId = Guid.NewGuid();
        if (failureOffset == PageSize)
        {
            SetupPage(repository, serverId, 0, Enumerable.Range(0, PageSize)
                .Select(index => Assignment(Guid.NewGuid(), $"page_{index}"))
                .ToList());
        }
        SetupFailure(repository, serverId, failureOffset, missingData);
        var activities = Create(repository);
        var statusCode = missingData ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            activities.GetReservedConfigVariables(new ConfigSlotInput(serverId, Guid.NewGuid(), "selected_variable")));

        Assert.Equal($"Could not determine configuration-variable ownership: {statusCode}.", exception.Message);
        VerifyRequests(repository, serverId, failureOffset == PageSize ? [0, PageSize] : [0]);
    }

    private static MapRotationActivities Create(Mock<IRepositoryApiClient> repository)
    {
        return new(Mock.Of<ILogger<MapRotationActivities>>(), repository.Object, Mock.Of<IServersApiClient>());
    }

    private static MapRotationServerAssignmentDto Assignment(
        Guid id,
        string? variable,
        DeploymentState state = DeploymentState.Synced)
    {
        return new(id, Guid.NewGuid(), Guid.NewGuid(), state, ActivationState.Active, 1, 1, "server.cfg", variable,
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
