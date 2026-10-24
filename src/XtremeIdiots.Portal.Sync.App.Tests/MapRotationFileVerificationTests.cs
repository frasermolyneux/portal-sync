using System.Net;
using Microsoft.Extensions.Logging;
using Moq;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Maps;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Config;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Sync.App.MapRotations;

namespace XtremeIdiots.Portal.Sync.App.Tests;

[Trait("Category", "Unit")]
public class MapRotationFileVerificationTests
{
    private readonly Mock<IServersApiClient> servers = new() { DefaultValue = DefaultValue.Mock };
    private MapRotationActivities Create() => new(Mock.Of<ILogger<MapRotationActivities>>(),
        Mock.Of<IRepositoryApiClient>(), servers.Object);

    [Fact]
    public async Task LinkedSlotLookupPaginatesAndExcludesOtherRotationsAndIndependentSlots()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var server = Guid.NewGuid();
        var rotation = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var linked = Guid.NewGuid();
        var now = DateTime.UtcNow;
        MapRotationServerAssignmentDto Assignment(Guid id, Guid rotationId, string variable)
        {
            return new(id, rotationId, server, DeploymentState.Synced, ActivationState.Active, 1, 1,
                "server.cfg", variable, null, null, now, now, null);
        }
        var firstPage = Enumerable.Range(0, 100)
            .Select(_ => Assignment(Guid.NewGuid(), Guid.NewGuid(), "scr_aacp_maps_1")).ToList();
        var secondPage = new[]
        {
            Assignment(linked, rotation, "scr_aacp_maps_1"),
            Assignment(Guid.NewGuid(), Guid.NewGuid(), "scr_aacp_maps_2"),
            Assignment(Guid.NewGuid(), rotation, "scr_small_rotation")
        };
        var api = Mock.Get(repository.Object.MapRotations.V1);
        api.Setup(x => x.GetServerAssignments(null, server, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(new CollectionModel<MapRotationServerAssignmentDto>(firstPage))));
        api.Setup(x => x.GetServerAssignments(null, server, null, 100, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(new CollectionModel<MapRotationServerAssignmentDto>(secondPage))));
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(), repository.Object, servers.Object);

        var result = await activities.GetLinkedConfigurationAssignments(new LinkedConfigurationInput(server, rotation, selected, "sv_maprotation"));

        var target = Assert.Single(result);
        Assert.Equal(linked, target.AssignmentId);
        Assert.Equal("server.cfg", target.ConfigFilePath);
        Assert.Equal("scr_aacp_maps_1", target.ConfigVariableName);
        Assert.Empty(await activities.GetLinkedConfigurationAssignments(
            new LinkedConfigurationInput(server, rotation, selected, "scr_small_rotation")));
        var reserved = await activities.GetReservedConfigVariables(new ConfigSlotInput(server, selected, "server.cfg", "scr_aacp_maps_1"));
        Assert.Contains("scr_aacp_maps_2", reserved);
        Assert.DoesNotContain("scr_aacp_maps_1", reserved);
    }

    [Fact]
    public async Task RepairSkipsTransfersOnlyAfterActualFileVerificationSucceeds()
    {
        Mock.Get(servers.Object.Maps.V1).Setup(x => x.VerifyServerMaps(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new ApiResult<MapVerificationCollectionDto>(HttpStatusCode.OK,
                new ApiResponse<MapVerificationCollectionDto>(new MapVerificationCollectionDto([new("mp_custom", true)]))));

        var result = await Create().RepairSingleMapToServer(new SyncMapInput(Guid.NewGuid(), "mp_custom", GameType.CallOfDuty4));

        Assert.True(result.Success);
        Assert.Equal(SkipReasons.AlreadyVerified, result.SkipReason);
        Mock.Get(servers.Object.Maps.V1).Verify(x => x.PushServerMapToHost(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task VerifyUsesFileIntegrityResultsNotDirectoryNames()
    {
        Mock.Get(servers.Object.Maps.V1).Setup(x => x.VerifyServerMaps(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new ApiResult<MapVerificationCollectionDto>(HttpStatusCode.OK,
                new ApiResponse<MapVerificationCollectionDto>(new MapVerificationCollectionDto([new("mp_empty", false)]))));

        var result = await Create().VerifyMapFiles(new VerifyMapFilesInput(Guid.NewGuid(), ["mp_empty"]));

        Assert.False(Assert.Single(result).Success);
        Mock.Get(servers.Object.Maps.V1).Verify(x => x.GetLoadedServerMapsFromHost(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task VerifyRejectsMissingResultEntries()
    {
        Mock.Get(servers.Object.Maps.V1).Setup(x => x.VerifyServerMaps(It.IsAny<Guid>(), It.IsAny<List<string>>()))
            .ReturnsAsync(new ApiResult<MapVerificationCollectionDto>(HttpStatusCode.OK,
                new ApiResponse<MapVerificationCollectionDto>(new MapVerificationCollectionDto([]))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Create().VerifyMapFiles(new VerifyMapFilesInput(Guid.NewGuid(), ["mp_missing"])));
    }

    [Fact]
    public async Task BadRequestDoesNotBecomeSuccessfulMapSkip()
    {
        Mock.Get(servers.Object.Maps.V1).Setup(x => x.PushServerMapToHost(It.IsAny<Guid>(), "mp_custom"))
            .ReturnsAsync(new ApiResult(HttpStatusCode.BadRequest));

        var result = await Create().SyncSingleMapToServer(new SyncMapInput(Guid.NewGuid(), "mp_custom",
            GameType.CallOfDuty4));

        Assert.False(result.Success);
        Assert.Null(result.SkipReason);
    }

    [Theory]
    [InlineData("expected", true)]
    [InlineData("old", false)]
    public async Task ConfigWriteRequiresExactReadBack(string retainedValue, bool success)
    {
        var id = Guid.NewGuid();
        Mock.Get(servers.Object.Config.V1).Setup(x => x.UpdateConfigVariable(id, "server.cfg", "sv_maprotation", "expected", null))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        Mock.Get(servers.Object.Config.V1).Setup(x => x.GetConfigFile(id, "server.cfg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigFileContentDto>(HttpStatusCode.OK,
                new ApiResponse<ConfigFileContentDto>(new ConfigFileContentDto("server.cfg", $"set sv_maprotation \"{retainedValue}\""))));
        var input = new WriteConfigInput(id, "server.cfg", "sv_maprotation", "expected");
        if (success)
        {
            await Create().WriteAndVerifyConfigVariable(input);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Create().WriteAndVerifyConfigVariable(input));
        }
    }
}
