using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MX.Api.Abstractions;
using XtremeIdiots.Portal.Integrations.Servers.Api.Client.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.MapRotations;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

public class MapRotationActivities(
    ILogger<MapRotationActivities> logger,
    IRepositoryApiClient repositoryApiClient,
    IServersApiClient serversApiClient)
{
    private readonly ILogger<MapRotationActivities> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly IServersApiClient serversApiClient = serversApiClient ?? throw new ArgumentNullException(nameof(serversApiClient));

    [Function(nameof(SyncSingleMapToServer))]
    public async Task<MapOperationResult> SyncSingleMapToServer(
        [ActivityTrigger] SyncMapInput input)
    {
        try
        {
            logger.LogInformation("Pushing map {MapName} to server {GameServerId}", input.MapName, input.GameServerId);
            var result = await serversApiClient.Maps.V1.PushServerMapToHost(input.GameServerId, input.MapName).ConfigureAwait(false);
            return new MapOperationResult(input.MapName, result.IsSuccess);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push map {MapName} to server {GameServerId}", input.MapName, input.GameServerId);
            return new MapOperationResult(input.MapName, false, ex.Message);
        }
    }

    [Function(nameof(RemoveSingleMapFromServer))]
    public async Task<MapOperationResult> RemoveSingleMapFromServer(
        [ActivityTrigger] RemoveMapInput input)
    {
        try
        {
            logger.LogInformation("Removing map {MapName} from server {GameServerId}", input.MapName, input.GameServerId);
            var result = await serversApiClient.Maps.V1.DeleteServerMapFromHost(input.GameServerId, input.MapName).ConfigureAwait(false);
            return new MapOperationResult(input.MapName, result.IsSuccess);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to remove map {MapName} from server {GameServerId}", input.MapName, input.GameServerId);
            return new MapOperationResult(input.MapName, false, ex.Message);
        }
    }

    [Function(nameof(GetLoadedMapsFromServer))]
    public async Task<List<string>> GetLoadedMapsFromServer(
        [ActivityTrigger] GetLoadedMapsInput input)
    {
        var result = await serversApiClient.Maps.V1.GetLoadedServerMapsFromHost(input.GameServerId).ConfigureAwait(false);
        if (!result.IsSuccess || result.Result?.Data?.Items is null)
        {
            throw new InvalidOperationException($"Failed to get loaded maps from server {input.GameServerId}. Cannot verify map presence.");
        }

        return result.Result.Data.Items.Select(m => m.Name).ToList();
    }

    [Function(nameof(GetMapsInOtherActiveRotations))]
    public async Task<List<string>> GetMapsInOtherActiveRotations(
        [ActivityTrigger] GetSharedMapsInput input)
    {
        var assignmentsResult = await repositoryApiClient.MapRotations.V1
            .GetServerAssignments(null, input.GameServerId, null, 0, 100).ConfigureAwait(false);

        if (!assignmentsResult.IsSuccess || assignmentsResult.Result?.Data?.Items is null)
            throw new InvalidOperationException($"Failed to query server assignments for server {input.GameServerId}. Cannot safely determine shared maps.");

        var sharedMapNames = new List<string>();

        foreach (var assignment in assignmentsResult.Result.Data.Items)
        {
            if (assignment.MapRotationServerAssignmentId == input.ExcludeAssignmentId)
                continue;

            if (assignment.DeploymentState == DeploymentState.Removed)
                continue;

            var rotationResult = await repositoryApiClient.MapRotations.V1
                .GetMapRotation(assignment.MapRotationId).ConfigureAwait(false);

            if (!rotationResult.IsSuccess || rotationResult.Result?.Data is null)
                throw new InvalidOperationException($"Failed to get rotation {assignment.MapRotationId} for active assignment {assignment.MapRotationServerAssignmentId}. Cannot safely determine shared maps.");

            if (rotationResult.Result.Data.MapRotationMaps is not null)
            {
                foreach (var rotationMap in rotationResult.Result.Data.MapRotationMaps)
                {
                    var mapResult = await repositoryApiClient.Maps.V1.GetMap(rotationMap.MapId).ConfigureAwait(false);
                    if (!mapResult.IsSuccess || mapResult.Result?.Data is null)
                        throw new InvalidOperationException($"Failed to resolve map {rotationMap.MapId} in rotation {assignment.MapRotationId}. Cannot safely determine shared maps.");

                    sharedMapNames.Add(mapResult.Result.Data.MapName);
                }
            }
        }

        return sharedMapNames.Distinct().ToList();
    }

    [Function(nameof(UpdateAssignmentStatus))]
    public async Task UpdateAssignmentStatus(
        [ActivityTrigger] UpdateStatusInput input)
    {
        try
        {
            var updateDto = new UpdateMapRotationServerAssignmentDto(input.AssignmentId)
            {
                DeploymentState = input.DeploymentState,
                ActivationState = input.ActivationState,
                DeployedVersion = input.DeployedVersion,
                ActivatedVersion = input.ActivatedVersion,
                LastError = input.LastError,
                LastErrorAt = input.LastErrorAt,
                UnassignedAt = input.UnassignedAt
            };

            await repositoryApiClient.MapRotations.V1.UpdateServerAssignment(updateDto).ConfigureAwait(false);
            logger.LogInformation("Updated assignment {AssignmentId} status", input.AssignmentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update assignment {AssignmentId} status", input.AssignmentId);
            throw;
        }
    }

    [Function(nameof(RecordOperation))]
    public async Task<Guid> RecordOperation(
        [ActivityTrigger] RecordOperationInput input)
    {
        try
        {
            var createDto = new CreateMapRotationAssignmentOperationDto(input.AssignmentId, input.OperationType)
            {
                DurableFunctionInstanceId = input.DurableFunctionInstanceId
            };

            var result = await repositoryApiClient.MapRotations.V1.CreateAssignmentOperation(createDto).ConfigureAwait(false);

            if (!result.IsSuccess || result.Result?.Data is null)
                throw new InvalidOperationException($"Failed to create operation for assignment {input.AssignmentId}");

            return result.Result.Data.MapRotationAssignmentOperationId;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record operation for assignment {AssignmentId}", input.AssignmentId);
            throw;
        }
    }

    [Function(nameof(CompleteOperation))]
    public async Task CompleteOperation(
        [ActivityTrigger] CompleteOperationInput input)
    {
        try
        {
            await repositoryApiClient.MapRotations.V1.UpdateAssignmentOperation(
                input.OperationId, input.Status, input.Error).ConfigureAwait(false);
            logger.LogInformation("Completed operation {OperationId} with status {Status}", input.OperationId, input.Status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to complete operation {OperationId}", input.OperationId);
            throw;
        }
    }

    [Function(nameof(GetRotationDetails))]
    public async Task<RotationDetails> GetRotationDetails(
        [ActivityTrigger] GetRotationDetailsInput input)
    {
        var assignmentResult = await repositoryApiClient.MapRotations.V1
            .GetServerAssignment(input.AssignmentId).ConfigureAwait(false);

        if (!assignmentResult.IsSuccess || assignmentResult.Result?.Data is null)
            throw new InvalidOperationException($"Failed to get server assignment {input.AssignmentId}");

        var assignment = assignmentResult.Result.Data;

        var rotationResult = await repositoryApiClient.MapRotations.V1
            .GetMapRotation(assignment.MapRotationId).ConfigureAwait(false);

        if (!rotationResult.IsSuccess || rotationResult.Result?.Data is null)
            throw new InvalidOperationException($"Failed to get map rotation {assignment.MapRotationId}");

        var rotation = rotationResult.Result.Data;
        var mapIds = rotation.MapRotationMaps?.Select(m => m.MapId).ToList() ?? [];

        return new RotationDetails(
            AssignmentId: assignment.MapRotationServerAssignmentId,
            GameServerId: assignment.GameServerId,
            MapRotationId: assignment.MapRotationId,
            DeploymentState: assignment.DeploymentState,
            ActivationState: assignment.ActivationState,
            DeployedVersion: assignment.DeployedVersion,
            RotationVersion: rotation.Version,
            ContentHash: rotation.ContentHash,
            ConfigFilePath: assignment.ConfigFilePath,
            ConfigVariableName: assignment.ConfigVariableName,
            GameMode: rotation.GameMode,
            MapIds: mapIds);
    }

    [Function(nameof(ResolveMapNames))]
    public async Task<List<string>> ResolveMapNames(
        [ActivityTrigger] ResolveMapNamesInput input)
    {
        var mapNames = new List<string>();

        foreach (var mapId in input.MapIds)
        {
            var mapResult = await repositoryApiClient.Maps.V1.GetMap(mapId).ConfigureAwait(false);
            if (mapResult.IsSuccess && mapResult.Result?.Data is not null)
            {
                mapNames.Add(mapResult.Result.Data.MapName);
            }
            else
            {
                throw new InvalidOperationException($"Failed to resolve map name for map ID {mapId}. All maps must be resolvable.");
            }
        }

        return mapNames;
    }

    [Function(nameof(FormatRotationString))]
    public Task<string> FormatRotationString(
        [ActivityTrigger] FormatRotationInput input)
    {
        string result;

        if (input.ConfigVariableName.StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase))
        {
            // Semicolon-separated format
            result = string.Join(";", input.MapNames);
        }
        else
        {
            // Standard rotation format: gametype {gameMode} map {map1} map {map2} ...
            var mapEntries = string.Join(" ", input.MapNames.Select(m => $"map {m}"));
            result = $"gametype {input.GameMode} {mapEntries}";
        }

        return Task.FromResult(result);
    }

    [Function(nameof(WriteConfigVariable))]
    public async Task<MapOperationResult> WriteConfigVariable(
        [ActivityTrigger] WriteConfigInput input)
    {
        try
        {
            logger.LogInformation("Writing config variable {ConfigVariableName} in {ConfigFilePath} on server {GameServerId}",
                input.ConfigVariableName, input.ConfigFilePath, input.GameServerId);

            var result = await serversApiClient.Config.V1
                .UpdateConfigVariable(input.GameServerId, input.ConfigFilePath, input.ConfigVariableName, input.Value)
                .ConfigureAwait(false);

            return new MapOperationResult(input.ConfigVariableName, result.IsSuccess,
                result.IsSuccess ? null : "Config API returned failure");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write config variable {ConfigVariableName} on server {GameServerId}",
                input.ConfigVariableName, input.GameServerId);
            return new MapOperationResult(input.ConfigVariableName, false, ex.Message);
        }
    }

    [Function(nameof(SetRconDvar))]
    public async Task<MapOperationResult> SetRconDvar(
        [ActivityTrigger] SetRconDvarInput input)
    {
        try
        {
            logger.LogInformation("Setting RCON dvar {DvarName} on server {GameServerId}",
                input.DvarName, input.GameServerId);

            var result = await serversApiClient.Rcon.V1
                .SetDvar(input.GameServerId, input.DvarName, input.Value)
                .ConfigureAwait(false);

            return new MapOperationResult(input.DvarName, result.IsSuccess,
                result.IsSuccess ? null : "RCON API returned failure");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set RCON dvar {DvarName} on server {GameServerId}",
                input.DvarName, input.GameServerId);
            return new MapOperationResult(input.DvarName, false, ex.Message);
        }
    }
}
