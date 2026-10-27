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

    [Theory]
    [InlineData(1)]
    [InlineData(91)]
    public async Task RepeatedGroupUnassignmentReconcilesRemovedTargetsWithoutPurgingHistory(int ageDays)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var api = Mock.Get(repository.Object.MapRotations.V1);
        var serverId = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var snapshots = new List<RotationDetails>();
        foreach (var variable in new[] { "sv_maprotation", "scr_aacp_maps_1" })
        {
            var id = Guid.NewGuid();
            var assignment = new MapRotationServerAssignmentDto(id, rotationId, serverId, DeploymentState.Removed,
                ActivationState.Inactive, 1, 1, "server.cfg", variable, null, null, now, now, now.AddDays(-ageDays));
            api.Setup(x => x.GetServerAssignment(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(HttpStatusCode.OK, new ApiResponse<MapRotationServerAssignmentDto>(assignment)));
            snapshots.Add(new(id, serverId, rotationId, GameType.CallOfDuty5, DeploymentState.Synced,
                ActivationState.Inactive, 1, 1, null, "server.cfg", variable, "dm", []));
        }
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(), repository.Object, servers.Object);

        await activities.UnassignConfigurationGroup(snapshots);

        api.Verify(x => x.DeleteServerAssignment(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(true, false, "none")]
    [InlineData(false, true, "none")]
    [InlineData(false, false, "path")]
    [InlineData(false, false, "variable")]
    [InlineData(false, false, "server")]
    public async Task ConfigurationGroupExcludesOtherRotationsAndFindsOldOutstandingOperations(bool outstanding, bool uppercase, string drift)
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var api = Mock.Get(repository.Object.MapRotations.V1);
        var server = Guid.NewGuid();
        var rotationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        MapRotationServerAssignmentDto Assignment(string variable, Guid rotation)
        {
            return new(Guid.NewGuid(), rotation, server, DeploymentState.Synced, ActivationState.Active,
                1, 1, "server.cfg", variable, null, null, now, now, null);
        }

        var normal = Assignment(uppercase ? "SV_MAPROTATION" : "sv_maprotation", rotationId);
        var picker = Assignment(uppercase ? "SCR_AACP_MAPS_1" : "scr_aacp_maps_1", rotationId);
        var other = Assignment("scr_aacp_maps_1", Guid.NewGuid());
        var custom = Assignment("scr_small_rotation", rotationId);
        var rotationDto = new MapRotationDto(rotationId, GameType.CallOfDuty5, "Synthetic", null, "dm", 1,
            null, now, now, [], [normal, picker, other, custom]);
        var gameServer = Newtonsoft.Json.JsonConvert.DeserializeObject<Portal.Repository.Abstractions.Models.V1.GameServers.GameServerDto>(
            Newtonsoft.Json.JsonConvert.SerializeObject(new { GameServerId = server, GameType = GameType.CallOfDuty5 }));
        repository.Setup(x => x.GameServers.V1.GetGameServer(server, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<Portal.Repository.Abstractions.Models.V1.GameServers.GameServerDto>(
                HttpStatusCode.OK, new ApiResponse<Portal.Repository.Abstractions.Models.V1.GameServers.GameServerDto>(gameServer)));
        api.Setup(x => x.GetMapRotation(rotationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<MapRotationDto>(HttpStatusCode.OK, new ApiResponse<MapRotationDto>(rotationDto)));
        foreach (var assignment in new[] { normal, picker, other, custom })
        {
            api.Setup(x => x.GetServerAssignment(assignment.MapRotationServerAssignmentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(HttpStatusCode.OK,
                    new ApiResponse<MapRotationServerAssignmentDto>(assignment)));
        }
        if (drift != "none")
        {
            var changed = picker with
            {
                ConfigFilePath = drift == "path" ? "changed.cfg" : picker.ConfigFilePath,
                ConfigVariableName = drift == "variable" ? "scr_small_rotation" : picker.ConfigVariableName,
                GameServerId = drift == "server" ? Guid.NewGuid() : picker.GameServerId
            };
            api.SetupSequence(x => x.GetServerAssignment(picker.MapRotationServerAssignmentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(HttpStatusCode.OK, new ApiResponse<MapRotationServerAssignmentDto>(picker)))
                .ReturnsAsync(new ApiResult<MapRotationServerAssignmentDto>(HttpStatusCode.OK, new ApiResponse<MapRotationServerAssignmentDto>(changed)));
        }
        api.Setup(x => x.GetServerAssignments(rotationId, server, null, 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<CollectionModel<MapRotationServerAssignmentDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<MapRotationServerAssignmentDto>>(new CollectionModel<MapRotationServerAssignmentDto>([picker, other, custom, normal]))));
        api.Setup(x => x.GetAssignmentOperations(It.IsAny<Guid>(), 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, int _, int _, CancellationToken _) =>
                new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(HttpStatusCode.OK,
                    new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(new CollectionModel<MapRotationAssignmentOperationDto>(
                        Enumerable.Range(0, 100).Select(_ => new MapRotationAssignmentOperationDto(Guid.NewGuid(),
                            id, AssignmentOperationType.Verify, AssignmentOperationStatus.Succeeded, null, now, now, null)).ToList()))));
        api.Setup(x => x.GetAssignmentOperations(It.IsAny<Guid>(), 100, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid id, int _, int _, CancellationToken _) =>
                new ApiResult<CollectionModel<MapRotationAssignmentOperationDto>>(HttpStatusCode.OK,
                    new ApiResponse<CollectionModel<MapRotationAssignmentOperationDto>>(new CollectionModel<MapRotationAssignmentOperationDto>(outstanding
                        ? [new(Guid.NewGuid(), id, AssignmentOperationType.Sync, AssignmentOperationStatus.InProgress, null, now, null, null)]
                        : []))));
        var activities = new MapRotationActivities(Mock.Of<ILogger<MapRotationActivities>>(), repository.Object, servers.Object);

        if (outstanding || drift != "none")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => activities.GetConfigurationGroup(new(picker.MapRotationServerAssignmentId)));
        }
        else
        {
            var group = await activities.GetConfigurationGroup(new(picker.MapRotationServerAssignmentId));
            Assert.Equal([normal.MapRotationServerAssignmentId, picker.MapRotationServerAssignmentId], group.Select(member => member.AssignmentId));
            api.Verify(x => x.GetAssignmentOperations(normal.MapRotationServerAssignmentId, 100, 100, It.IsAny<CancellationToken>()), Times.Once);
            api.Verify(x => x.GetAssignmentOperations(picker.MapRotationServerAssignmentId, 100, 100, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task LinkedSlotLookupPaginatesAndExcludesOtherRotationsAndIndependentSlots()
    {
        var repository = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var server = Guid.NewGuid();
        var rotation = Guid.NewGuid();
        var selected = Guid.NewGuid();
        var linked = Guid.NewGuid();
        var now = DateTime.UtcNow;
        MapRotationServerAssignmentDto Assignment(Guid id, Guid rotationId, string variable,
            DeploymentState state = DeploymentState.Synced, string path = "server.cfg")
        {
            return new(id, rotationId, server, state, ActivationState.Active, 1, 1,
                path, variable, null, null, now, now, null);
        }
        var firstPage = Enumerable.Range(0, 100)
            .Select(_ => Assignment(Guid.NewGuid(), Guid.NewGuid(), "scr_aacp_maps_1")).ToList();
        var secondPage = new[]
        {
            Assignment(linked, rotation, "scr_aacp_maps_1"),
            Assignment(Guid.NewGuid(), Guid.NewGuid(), "scr_aacp_maps_2"),
            Assignment(Guid.NewGuid(), Guid.NewGuid(), "scr_aacp_maps_3", DeploymentState.Removing, "other.cfg"),
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
        var reserved = await activities.GetReservedConfigVariables(new ConfigSlotInput(server, selected, "scr_aacp_maps_1"));
        Assert.Contains("scr_aacp_maps_2", reserved);
        Assert.Contains("scr_aacp_maps_3", reserved);
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
    [InlineData("set sv_maprotation \"expected\"; set sv_maprotation \"stale\"", "expected", false)]
    [InlineData("set sv_maprotation \"\"; set sv_maprotation \"stale\"", "", false)]
    [InlineData("set sv_maprotation \"expected\"\nseta sv_maprotation \"stale\"", "expected", false)]
    [InlineData("set sv_maprotation \"expected\"\nsv_maprotation \"stale\"", "expected", false)]
    [InlineData("/*\nset sv_maprotation \"expected\"\n*/", "expected", false)]
    [InlineData("set sv_maprotation \"expected\" // set sv_maprotation \"stale\";", "expected", true)]
    [InlineData("/* set sv_maprotation \"stale\" */\nset sv_maprotation \"expected\";", "expected", true)]
    [InlineData("set sv_maprotation \"mp_a;mp_b\"", "mp_a;mp_b", true)]
    [InlineData("set hostname \"// not a comment\"; set sv_maprotation \"expected\"", "expected", true)]
    public async Task ConfigReadBackFindsAllExecutableDefinitionsWithoutCountingCommentsOrQuotedSeparators(
        string config, string expected, bool success)
    {
        var server = Guid.NewGuid();
        var api = Mock.Get(servers.Object.Config.V1);
        api.Setup(x => x.UpdateConfigVariable(server, "server.cfg", "sv_maprotation", expected, null))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        api.Setup(x => x.GetConfigFile(server, "server.cfg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigFileContentDto>(HttpStatusCode.OK,
                new ApiResponse<ConfigFileContentDto>(new ConfigFileContentDto("server.cfg", config))));
        var input = new WriteConfigInput(server, "server.cfg", "sv_maprotation", expected);
        if (success)
        {
            await Create().WriteAndVerifyConfigVariable(input);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => Create().WriteAndVerifyConfigVariable(input));
        }
    }

    [Fact]
    public async Task ClearCannotIgnoreADefinitionAfterAnotherCommandOnTheSameLine()
    {
        var server = Guid.NewGuid();
        var api = Mock.Get(servers.Object.Config.V1);
        api.Setup(x => x.UpdateConfigVariable(server, "server.cfg", "sv_maprotation", "", null))
            .ReturnsAsync(new ApiResult(HttpStatusCode.OK));
        api.Setup(x => x.GetConfigFile(server, "server.cfg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiResult<ConfigFileContentDto>(HttpStatusCode.OK,
                new ApiResponse<ConfigFileContentDto>(new ConfigFileContentDto("server.cfg",
                    "set other \"value\"; set sv_maprotation \"stale\""))));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create().ClearConfigVariableIfPresent(
            new WriteConfigInput(server, "server.cfg", "sv_maprotation", "")));

        api.Verify(x => x.UpdateConfigVariable(server, "server.cfg", "sv_maprotation", "", null), Times.Once);
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
