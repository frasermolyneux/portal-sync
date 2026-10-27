using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;
using MX.Api.Abstractions;
using System.Net;
using XtremeIdiots.Portal.Integrations.Servers.Abstractions.Models.V1.Rcon;
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
    private static readonly Action<ILogger, string, Guid, string, Exception?> UnsupportedRconDvarUpdateLogger =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Warning,
            new EventId(0),
            "Skipping RCON dvar update {DvarName} on server {GameServerId}: {UnsupportedError}");

    private readonly ILogger<MapRotationActivities> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
    private readonly IServersApiClient serversApiClient = serversApiClient ?? throw new ArgumentNullException(nameof(serversApiClient));

    [Function(nameof(SyncSingleMapToServer))]
    public async Task<MapOperationResult> SyncSingleMapToServer(
        [ActivityTrigger] SyncMapInput input)
    {
        if (BuiltInMaps.IsBuiltIn(input.GameType, input.MapName))
        {
            logger.LogInformation("Skipping built-in map {MapName} for server {GameServerId}", input.MapName, input.GameServerId);
            return new MapOperationResult(input.MapName, true, SkipReason: SkipReasons.BuiltInMap);
        }

        try
        {
            logger.LogInformation("Pushing map {MapName} to server {GameServerId}", input.MapName, input.GameServerId);
            var result = await serversApiClient.Maps.V1.PushServerMapToHost(input.GameServerId, input.MapName).ConfigureAwait(false);

            return new MapOperationResult(input.MapName, result.IsSuccess,
                Error: result.IsSuccess ? null : $"Failed to push map: {result.StatusCode}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to push map {MapName} to server {GameServerId}", input.MapName, input.GameServerId);
            return new MapOperationResult(input.MapName, false, ex.Message);
        }
    }

    [Function(nameof(RepairSingleMapToServer))]
    public async Task<MapOperationResult> RepairSingleMapToServer([ActivityTrigger] SyncMapInput input)
    {
        if (!BuiltInMaps.IsBuiltIn(input.GameType, input.MapName))
        {
            var verification = await VerifyMapFiles(new VerifyMapFilesInput(input.GameServerId, [input.MapName])).ConfigureAwait(false);
            if (verification.All(result => result.Success))
            {
                return new MapOperationResult(input.MapName, true, SkipReason: SkipReasons.AlreadyVerified);
            }
        }
        return await SyncSingleMapToServer(input).ConfigureAwait(false);
    }

    [Function(nameof(VerifyMapFiles))]
    public async Task<List<MapOperationResult>> VerifyMapFiles([ActivityTrigger] VerifyMapFilesInput input)
    {
        var names = input.MapNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var response = await serversApiClient.Maps.V1.VerifyServerMaps(input.GameServerId, names).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Data?.Items is not { } items)
        {
            throw new InvalidOperationException($"File verification failed for server {input.GameServerId}: {response.StatusCode}.");
        }

        if (items.Count() != names.Count || names.Any(name =>
            items.Count(item => string.Equals(item.MapName, name, StringComparison.OrdinalIgnoreCase)) != 1))
        {
            throw new InvalidOperationException("File verification returned an incomplete or ambiguous map result.");
        }

        return items.Select(item => new MapOperationResult(item.MapName, item.ExistsOnServer,
            item.ExistsOnServer ? null : "Required map files are missing or have an incorrect size.")).ToList();
    }

    [Function(nameof(RemoveSingleMapFromServer))]
    public async Task<MapOperationResult> RemoveSingleMapFromServer(
        [ActivityTrigger] RemoveMapInput input)
    {
        if (BuiltInMaps.IsBuiltIn(input.GameType, input.MapName))
        {
            logger.LogInformation("Skipping removal of built-in map {MapName} for server {GameServerId}", input.MapName, input.GameServerId);
            return new MapOperationResult(input.MapName, true, SkipReason: SkipReasons.BuiltInMap);
        }

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
        {
            throw new InvalidOperationException($"Failed to query server assignments for server {input.GameServerId}. Cannot safely determine shared maps.");
        }

        var sharedMapNames = new List<string>();

        foreach (var assignment in assignmentsResult.Result.Data.Items)
        {
            if (assignment.MapRotationServerAssignmentId == input.ExcludeAssignmentId)
            {
                continue;
            }

            if (assignment.DeploymentState == DeploymentState.Removed)
            {
                continue;
            }

            var rotationResult = await repositoryApiClient.MapRotations.V1
                .GetMapRotation(assignment.MapRotationId).ConfigureAwait(false);

            if (!rotationResult.IsSuccess || rotationResult.Result?.Data is null)
            {
                throw new InvalidOperationException($"Failed to get rotation {assignment.MapRotationId} for active assignment {assignment.MapRotationServerAssignmentId}. Cannot safely determine shared maps.");
            }

            if (rotationResult.Result.Data.MapRotationMaps is not null)
            {
                foreach (var rotationMap in rotationResult.Result.Data.MapRotationMaps)
                {
                    var mapResult = await repositoryApiClient.Maps.V1.GetMap(rotationMap.MapId).ConfigureAwait(false);
                    if (!mapResult.IsSuccess || mapResult.Result?.Data is null)
                    {
                        throw new InvalidOperationException($"Failed to resolve map {rotationMap.MapId} in rotation {assignment.MapRotationId}. Cannot safely determine shared maps.");
                    }

                    sharedMapNames.Add(mapResult.Result.Data.MapName);
                }
            }
        }

        return sharedMapNames.Distinct().ToList();
    }

    [Function(nameof(GetLinkedConfigurationAssignments))]
    public async Task<List<LinkedConfigurationTarget>> GetLinkedConfigurationAssignments([ActivityTrigger] LinkedConfigurationInput input)
    {
        var counterpart = string.Equals(input.Variable, "sv_maprotation", StringComparison.OrdinalIgnoreCase)
            ? "scr_aacp_maps_1"
            : string.Equals(input.Variable, "scr_aacp_maps_1", StringComparison.OrdinalIgnoreCase)
                ? "sv_maprotation" : null;
        if (counterpart is null)
        {
            return [];
        }
        var linked = new List<LinkedConfigurationTarget>();
        var skip = 0;
        while (true)
        {
            var result = await repositoryApiClient.MapRotations.V1
                .GetServerAssignments(null, input.GameServerId, null, skip, 100).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException($"Could not load linked configuration slots for server {input.GameServerId}: {result.StatusCode}.");
            }
            linked.AddRange(items.Where(assignment =>
                assignment.MapRotationServerAssignmentId != input.AssignmentId &&
                assignment.MapRotationId == input.RotationId &&
                assignment.DeploymentState is not (DeploymentState.Removed or DeploymentState.Removing) &&
                string.Equals(assignment.ConfigVariableName, counterpart, StringComparison.OrdinalIgnoreCase))
                .Select(assignment => new LinkedConfigurationTarget(assignment.MapRotationServerAssignmentId,
                    assignment.ConfigFilePath, assignment.ConfigVariableName)));
            if (items.Count() < 100)
            {
                return linked;
            }
            skip += 100;
        }
    }

    [Function(nameof(GetConfigurationGroup))]
    public async Task<List<RotationDetails>> GetConfigurationGroup([ActivityTrigger] GetRotationDetailsInput input)
    {
        var selected = await GetRotationDetails(input).ConfigureAwait(false);
        var targets = await ReadConfigurationTargets(selected).ConfigureAwait(false);
        if (targets.Count == 0 || targets.All(target => target.MapRotationServerAssignmentId != input.AssignmentId)
            || targets.GroupBy(target => target.ConfigVariableName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("The configuration group is missing or ambiguous. Resolve duplicate targets before applying.");
        }
        var group = new List<RotationDetails>();
        foreach (var target in targets.OrderBy(target => string.Equals(target.ConfigVariableName, "sv_maprotation", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            await CheckOutstandingConfigurationOperations(target.MapRotationServerAssignmentId).ConfigureAwait(false);
            var details = await GetRotationDetails(new GetRotationDetailsInput(target.MapRotationServerAssignmentId)).ConfigureAwait(false);
            if (details.MapRotationId != target.MapRotationId || details.GameServerId != target.GameServerId
                || details.ConfigFilePath != target.ConfigFilePath
                || !string.Equals(details.ConfigVariableName, target.ConfigVariableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A configuration target changed during group discovery. Review the server assignment and retry.");
            }
            group.Add(details);
        }
        return group;
    }

    private async Task<List<MapRotationServerAssignmentDto>> ReadConfigurationTargets(RotationDetails selected)
    {
        var targets = new List<MapRotationServerAssignmentDto>();
        var standard = IsStandardConfiguration(selected.ConfigVariableName);
        for (var skip = 0; ; skip += 100)
        {
            var result = await repositoryApiClient.MapRotations.V1
                .GetServerAssignments(selected.MapRotationId, selected.GameServerId, null, skip, 100).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException("Unable to load the server's configuration group.");
            }
            targets.AddRange(items.Where(assignment => assignment.DeploymentState != DeploymentState.Removed
                && assignment.MapRotationId == selected.MapRotationId && assignment.GameServerId == selected.GameServerId
                && (standard ? IsStandardConfiguration(assignment.ConfigVariableName)
                    : assignment.MapRotationServerAssignmentId == selected.AssignmentId)));
            if (items.Count() < 100)
            {
                break;
            }
        }
        return targets;
    }

    private static bool IsStandardConfiguration(string? variable) =>
        string.Equals(variable, "sv_maprotation", StringComparison.OrdinalIgnoreCase)
        || string.Equals(variable, "scr_aacp_maps_1", StringComparison.OrdinalIgnoreCase);

    private async Task CheckOutstandingConfigurationOperations(Guid assignmentId)
    {
        for (var skip = 0; ; skip += 100)
        {
            var operations = await repositoryApiClient.MapRotations.V1
                .GetAssignmentOperations(assignmentId, skip, 100).ConfigureAwait(false);
            if (!operations.IsSuccess || operations.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException("Unable to check outstanding operations for a configuration target.");
            }
            if (items.Any(operation => operation.Status == AssignmentOperationStatus.InProgress))
            {
                throw new InvalidOperationException("A configuration target still has an in-progress operation. Wait for its recorded outcome.");
            }
            if (items.Count() < 100)
            {
                return;
            }
        }
    }

    [Function(nameof(UnassignConfigurationGroup))]
    public async Task UnassignConfigurationGroup([ActivityTrigger] List<RotationDetails> snapshots)
    {
        if (snapshots.Count == 0)
        {
            throw new InvalidOperationException("The server assignment has no validated configuration targets.");
        }
        var currentAssignments = new List<MapRotationServerAssignmentDto>();
        foreach (var snapshot in snapshots)
        {
            var current = await repositoryApiClient.MapRotations.V1.GetServerAssignment(snapshot.AssignmentId).ConfigureAwait(false);
            if (!current.IsSuccess || current.Result?.Data is not { } assignment
                || assignment.MapRotationId != snapshot.MapRotationId || assignment.GameServerId != snapshot.GameServerId)
            {
                throw new InvalidOperationException("A configuration target changed or could not be read before unassignment.");
            }
            currentAssignments.Add(assignment);
        }
        if (currentAssignments.All(assignment => assignment.DeploymentState == DeploymentState.Removed
            && assignment.ActivationState == ActivationState.Inactive))
        {
            return;
        }
        if (currentAssignments.Any(assignment => assignment.DeploymentState == DeploymentState.Removed))
        {
            throw new InvalidOperationException("Only part of the configuration group is removed. Review its retained history before retrying.");
        }
        var group = await GetConfigurationGroup(new GetRotationDetailsInput(snapshots[0].AssignmentId)).ConfigureAwait(false);
        if (!group.Select(member => member.AssignmentId).Order().SequenceEqual(snapshots.Select(member => member.AssignmentId).Order()))
        {
            throw new InvalidOperationException("The configuration group changed before unassignment. Review the server assignment and retry.");
        }
        var result = await repositoryApiClient.MapRotations.V1.DeleteServerAssignment(snapshots[0].AssignmentId).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Could not unassign the configuration group: {result.StatusCode}. Deactivate both configurations first.");
        }
    }

    [Function(nameof(GetReservedConfigVariables))]
    public async Task<List<string>> GetReservedConfigVariables([ActivityTrigger] ConfigSlotInput input)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var skip = 0; ; skip += 100)
        {
            var result = await repositoryApiClient.MapRotations.V1
                .GetServerAssignments(null, input.GameServerId, null, skip, 100).ConfigureAwait(false);
            if (!result.IsSuccess || result.Result?.Data?.Items is not { } items)
            {
                throw new InvalidOperationException($"Could not determine configuration-variable ownership: {result.StatusCode}.");
            }
            foreach (var assignment in items.Where(assignment =>
                assignment.MapRotationServerAssignmentId != input.AssignmentId &&
                assignment.DeploymentState != DeploymentState.Removed &&
                !string.IsNullOrWhiteSpace(assignment.ConfigVariableName) &&
                !string.Equals(assignment.ConfigVariableName, input.ConfigVariableName, StringComparison.OrdinalIgnoreCase)))
            {
                reserved.Add(assignment.ConfigVariableName!);
            }
            if (items.Count() < 100)
            {
                return reserved.ToList();
            }
        }
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

            var result = await repositoryApiClient.MapRotations.V1.UpdateServerAssignment(updateDto).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to update assignment {input.AssignmentId} status. API returned {(int)result.StatusCode} ({result.StatusCode}).");
            }

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
            {
                throw new InvalidOperationException($"Failed to create operation for assignment {input.AssignmentId}");
            }

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
            var result = await repositoryApiClient.MapRotations.V1.UpdateAssignmentOperation(
                input.OperationId, input.Status, input.Error).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Failed to complete operation {input.OperationId}. API returned {(int)result.StatusCode} ({result.StatusCode}).");
            }

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
        {
            throw new InvalidOperationException($"Failed to get server assignment {input.AssignmentId}");
        }

        var assignment = assignmentResult.Result.Data;

        var rotationResult = await repositoryApiClient.MapRotations.V1
            .GetMapRotation(assignment.MapRotationId).ConfigureAwait(false);

        if (!rotationResult.IsSuccess || rotationResult.Result?.Data is null)
        {
            throw new InvalidOperationException($"Failed to get map rotation {assignment.MapRotationId}");
        }

        var rotation = rotationResult.Result.Data;
        var serverResult = await repositoryApiClient.GameServers.V1
            .GetGameServer(assignment.GameServerId).ConfigureAwait(false);
        if (!serverResult.IsSuccess || serverResult.Result?.Data is null)
        {
            throw new InvalidOperationException($"Failed to get game server {assignment.GameServerId} for assignment {input.AssignmentId}");
        }

        var mapIds = rotation.MapRotationMaps?.Select(m => m.MapId).ToList() ?? [];

        return new RotationDetails(
            AssignmentId: assignment.MapRotationServerAssignmentId,
            GameServerId: assignment.GameServerId,
            MapRotationId: assignment.MapRotationId,
            GameType: serverResult.Result.Data.GameType,
            DeploymentState: assignment.DeploymentState,
            ActivationState: assignment.ActivationState,
            DeployedVersion: assignment.DeployedVersion,
            RotationVersion: rotation.Version,
            ContentHash: rotation.ContentHash,
            ConfigFilePath: assignment.ConfigFilePath,
            ConfigVariableName: assignment.ConfigVariableName,
            GameMode: rotation.GameMode,
            MapIds: mapIds,
            Title: rotation.Title);
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

    private const int MaxVariableLength = 1024;

    [Function(nameof(FormatRotationString))]
    public Task<FormatRotationOutput> FormatRotationString(
        [ActivityTrigger] FormatRotationInput input)
    {
        var isAacp = input.ConfigVariableName.StartsWith("scr_aacp_maps_", StringComparison.OrdinalIgnoreCase);

        if (isAacp)
        {
            return Task.FromResult(FormatAacpRotation(input.MapNames, input.ConfigVariableName));
        }

        return Task.FromResult(FormatStandardRotation(input.MapNames, input.GameMode, input.ConfigVariableName));
    }

    private static FormatRotationOutput FormatStandardRotation(List<string> mapNames, string gameMode, string baseVariable)
    {
        if (mapNames.Count == 0)
        {
            return new FormatRotationOutput([new RotationStringPart(baseVariable, $"gametype {gameMode}")]);
        }

        var prefix = $"gametype {gameMode} ";
        var mapEntries = mapNames.Select(m => $"map {m}").ToList();

        var parts = new List<RotationStringPart>();
        var currentChunk = prefix;
        var currentVarName = baseVariable;
        var suffixIndex = 1;

        foreach (var entry in mapEntries)
        {
            var candidate = currentChunk.Length == 0 ? entry : currentChunk + entry;

            if (candidate.Length > MaxVariableLength && currentChunk.TrimEnd().Length > 0 && currentChunk != prefix)
            {
                // Current chunk is full, emit it and start a new one
                parts.Add(new RotationStringPart(currentVarName, currentChunk.TrimEnd()));
                currentVarName = $"{baseVariable}_{suffixIndex}";
                suffixIndex++;
                currentChunk = entry + " ";
            }
            else
            {
                // Entry fits, or chunk is empty/prefix-only (must accept to avoid infinite loop)
                currentChunk = candidate + " ";
            }
        }

        // Emit final chunk
        if (currentChunk.Trim().Length > 0)
        {
            parts.Add(new RotationStringPart(currentVarName, currentChunk.TrimEnd()));
        }

        return new FormatRotationOutput(parts);
    }

    private static FormatRotationOutput FormatAacpRotation(List<string> mapNames, string baseVariable)
    {
        if (mapNames.Count == 0)
        {
            return new FormatRotationOutput([new RotationStringPart(baseVariable, "")]);
        }

        // Parse the base variable to determine the suffix series (e.g., scr_aacp_maps_1 -> root=scr_aacp_maps_, start=1)
        var (root, startIndex) = ParseVariableNameBase(baseVariable);

        var parts = new List<RotationStringPart>();
        var currentChunk = "";
        var currentIndex = startIndex;

        foreach (var mapName in mapNames)
        {
            var candidate = currentChunk.Length == 0 ? mapName : $"{currentChunk};{mapName}";

            if (candidate.Length > MaxVariableLength && currentChunk.Length > 0)
            {
                parts.Add(new RotationStringPart($"{root}{currentIndex}", currentChunk));
                currentIndex++;
                currentChunk = mapName;
            }
            else
            {
                // Entry fits, or chunk is empty (must accept to avoid infinite loop on oversized single entry)
                currentChunk = candidate;
            }
        }

        if (currentChunk.Length > 0)
        {
            parts.Add(new RotationStringPart($"{root}{currentIndex}", currentChunk));
        }

        return new FormatRotationOutput(parts);
    }

    private static (string Root, int StartIndex) ParseVariableNameBase(string variableName)
        => RotationVariableNaming.ParseVariableNameBase(variableName);

    [Function(nameof(WriteConfigVariable))]
    public async Task<MapOperationResult> WriteConfigVariable(
        [ActivityTrigger] WriteConfigInput input)
    {
        try
        {
            logger.LogInformation("Writing config variable {ConfigVariableName} in {ConfigFilePath} on server {GameServerId}",
                input.ConfigVariableName, input.ConfigFilePath, input.GameServerId);

            var result = await serversApiClient.Config.V1
                .UpdateConfigVariable(input.GameServerId, input.ConfigFilePath, input.ConfigVariableName, input.Value, input.CommentLines)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                return new MapOperationResult(input.ConfigVariableName, true);
            }

            var errorDetail = result.Result?.Errors?.FirstOrDefault()?.Message
                ?? result.Result?.Errors?.FirstOrDefault()?.Code
                ?? "Config API returned failure";

            logger.LogWarning("Config API returned failure for {ConfigVariableName} in {ConfigFilePath}: {Error}",
                input.ConfigVariableName, input.ConfigFilePath, errorDetail);

            return new MapOperationResult(input.ConfigVariableName, false, errorDetail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write config variable {ConfigVariableName} in {ConfigFilePath} on server {GameServerId}",
                input.ConfigVariableName, input.ConfigFilePath, input.GameServerId);
            return new MapOperationResult(input.ConfigVariableName, false, ex.Message);
        }
    }

    [Function(nameof(WriteAndVerifyConfigVariable))]
    public async Task WriteAndVerifyConfigVariable([ActivityTrigger] WriteConfigInput input)
    {
        var write = await WriteConfigVariable(input).ConfigureAwait(false);
        if (!write.Success)
        {
            throw new InvalidOperationException(write.Error);
        }

        var content = await ReadConfig(input).ConfigureAwait(false);
        var values = ConfigVariableValues(content, input.ConfigVariableName);
        if (values.Count != 1 || !string.Equals(values[0], input.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Config read-back did not confirm a unique '{input.ConfigVariableName}' value.");
        }
    }

    [Function(nameof(ClearConfigVariableIfPresent))]
    public async Task ClearConfigVariableIfPresent([ActivityTrigger] WriteConfigInput input)
    {
        var content = await ReadConfig(input).ConfigureAwait(false);
        if (ConfigVariableValues(content, input.ConfigVariableName).Count != 0)
        {
            await WriteAndVerifyConfigVariable(input with { Value = "" }).ConfigureAwait(false);
        }
    }

    [Function(nameof(PreflightConfigVariableForWrite))]
    public async Task<ConfigPreflightResult> PreflightConfigVariableForWrite([ActivityTrigger] ConfigPreflightInput input)
    {
        try
        {
            var target = input.Target;
            var response = await serversApiClient.Config.V1.GetConfigFile(target.GameServerId, target.ConfigFilePath).ConfigureAwait(false);
            if (input.AllowMissing && IsMissingConfigFile(response.StatusCode, response.Result?.Errors))
            {
                return new(false);
            }
            if (!response.IsSuccess || response.Result?.Errors?.Length > 0 || response.Result?.Data == null)
            {
                return new(false, $"Could not read configuration '{target.ConfigFilePath}': {response.StatusCode}.");
            }
            var content = response.Result.Data.Content;
            var values = ConfigVariableValues(content, target.ConfigVariableName);
            if (input.AllowMissing && values.Count == 0)
            {
                return new(false);
            }
            var declaration = new Regex($@"^\s*(?<command>set)\s+{Regex.Escape(target.ConfigVariableName)}\s+""[^""]*""",
                RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            var rawMatch = declaration.Match(content);
            var activeMatch = declaration.Match(MaskConfigCommentsAndQuotedValues(content));
            if (values.Count != 1 || !rawMatch.Success || !activeMatch.Success
                || rawMatch.Groups["command"].Index != activeMatch.Groups["command"].Index)
            {
                return new(false,
                    $"The file '{target.ConfigFilePath}' must contain exactly one active quoted 'set {target.ConfigVariableName}' declaration in a writable line format. Choose a different configuration file or correct the server file before retrying.");
            }
            return new(true);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    [Function(nameof(TryWriteAndVerifyConfigVariable))]
    public async Task<ConfigWriteResult> TryWriteAndVerifyConfigVariable([ActivityTrigger] WriteConfigInput input)
    {
        try
        {
            // The API client can retry internally; its final rejection cannot prove that an earlier attempt did not write.
            var response = await serversApiClient.Config.V1.UpdateConfigVariable(input.GameServerId, input.ConfigFilePath,
                input.ConfigVariableName, input.Value, input.CommentLines).ConfigureAwait(false);

            if (!response.IsSuccess || response.Result?.Errors?.Length > 0)
            {
                return new(false,
                    response.Result?.Errors?.FirstOrDefault()?.Message ?? $"Configuration write failed: {response.StatusCode}.");
            }

            var content = await ReadConfig(input).ConfigureAwait(false);
            var values = ConfigVariableValues(content, input.ConfigVariableName);
            return values.Count == 1 && string.Equals(values[0], input.Value, StringComparison.Ordinal)
                ? new(true)
                : new(false, $"Config read-back did not confirm a unique '{input.ConfigVariableName}' value.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Configuration write/read-back could not be confirmed for server {GameServerId}", input.GameServerId);
            return new(false, ex.Message);
        }
    }

    private static string MaskConfigCommentsAndQuotedValues(string content) =>
        Regex.Replace(content, @"""(?:\\[\s\S]|[^""\\])*""|//[^\r\n]*|/\*[\s\S]*?(?:\*/|$)", match =>
    {
        var characters = match.Value.ToCharArray();
        var quoted = characters[0] == '"';
        for (var index = quoted ? 1 : 0; index < characters.Length - (quoted ? 1 : 0); index++)
        {
            if (characters[index] is not ('\r' or '\n'))
            {
                characters[index] = ' ';
            }
        }
        return new string(characters);
    }, RegexOptions.None, TimeSpan.FromSeconds(1));

    private static bool IsMissingConfigFile(HttpStatusCode? status, ApiError[]? errors) =>
        status == HttpStatusCode.NotFound && errors?.Length > 0
        && errors.All(error => error.Code == "CONFIG_FILE_NOT_FOUND");

    private async Task<string> ReadConfig(WriteConfigInput input)
    {
        var response = await serversApiClient.Config.V1.GetConfigFile(input.GameServerId, input.ConfigFilePath).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result?.Data is not { } config)
        {
            throw new InvalidOperationException($"Could not read back configuration '{input.ConfigFilePath}': {response.StatusCode}.");
        }
        return config.Content;
    }

    private static List<string> ConfigVariableValues(string content, string name)
    {
        var definition = $@"^(?:(?:set|seta|sets|setu)\s+)?{Regex.Escape(name)}";
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var header = new Regex(definition + @"(?:\s|$)", options, TimeSpan.FromSeconds(1));
        var value = new Regex(definition + @"\s+""([^""]*)""\s*$", options, TimeSpan.FromSeconds(1));
        var values = new List<string>();
        foreach (var command in ConfigCommands(content).Where(command => header.IsMatch(command)))
        {
            var match = value.Match(command);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Configuration contains an unsupported '{name}' definition; read-back cannot be confirmed.");
            }
            values.Add(match.Groups[1].Value);
        }
        return values;
    }

    private static List<string> ConfigCommands(string content)
    {
        var commands = new List<string>();
        var buffer = new StringBuilder();
        var quoted = false;
        var lineComment = false;
        var blockComment = false;
        void FinishCommand()
        {
            var command = buffer.ToString().Trim();
            if (command.Length != 0)
            {
                commands.Add(command);
            }
            buffer.Length = 0;
        }
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            var next = index + 1 < content.Length ? content[index + 1] : '\0';
            if (lineComment)
            {
                if (character is '\r' or '\n')
                {
                    lineComment = false;
                    FinishCommand();
                }
                continue;
            }
            if (blockComment)
            {
                if (character == '*' && next == '/')
                {
                    blockComment = false;
                    index++;
                }
                continue;
            }
            if (quoted)
            {
                _ = buffer.Append(character);
                if (character == '\\' && next != '\0')
                {
                    _ = buffer.Append(next);
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (character == '/' && next is '/' or '*')
            {
                lineComment = next == '/';
                blockComment = next == '*';
                _ = buffer.Append(' ');
                index++;
                continue;
            }
            if (character is ';' or '\r' or '\n')
            {
                FinishCommand();
                continue;
            }
            quoted = character == '"';
            _ = buffer.Append(character);
        }
        if (quoted || blockComment)
        {
            throw new InvalidOperationException("Configuration contains unterminated quoted text or a block comment; read-back cannot be confirmed.");
        }
        FinishCommand();
        return commands;
    }

    [Function(nameof(SetRconDvar))]
    public async Task<MapOperationResult> SetRconDvar(
        [ActivityTrigger] SetRconDvarInput input)
    {
        try
        {
            logger.LogInformation("Setting RCON dvar {DvarName} on server {GameServerId}",
                input.DvarName, input.GameServerId);

            ApiResult<string> result;

            switch (input.GameType)
            {
                case GameType.CallOfDuty2:
                    result = await serversApiClient.Cod2Rcon.V1
                        .Set(input.GameServerId, new SetDvarRequest
                        {
                            DvarName = input.DvarName,
                            Value = input.Value
                        })
                        .ConfigureAwait(false);
                    break;

                case GameType.CallOfDuty4:
                    result = await serversApiClient.Cod4Rcon.V1
                        .Set(input.GameServerId, new SetDvarRequest
                        {
                            DvarName = input.DvarName,
                            Value = input.Value
                        })
                        .ConfigureAwait(false);
                    break;

                case GameType.CallOfDuty5:
                    result = await serversApiClient.Cod5Rcon.V1
                        .Set(input.GameServerId, new SetDvarRequest
                        {
                            DvarName = input.DvarName,
                            Value = input.Value
                        })
                        .ConfigureAwait(false);
                    break;

                case GameType.CallOfDuty4x:
                    result = await serversApiClient.CoD4xRcon.V1
                        .Set(input.GameServerId, new CoD4xSetDvarRequestDto
                        {
                            DvarName = input.DvarName,
                            Value = input.Value
                        })
                        .ConfigureAwait(false);
                    break;

                default:
                    var unsupportedError = $"RCON dvar updates are not supported for game type '{input.GameType}'.";
                    UnsupportedRconDvarUpdateLogger(
                        logger,
                        input.DvarName,
                        input.GameServerId,
                        unsupportedError,
                        null);
                    return new MapOperationResult(input.DvarName, false, unsupportedError);
            }

            if (result.IsSuccess)
            {
                return new MapOperationResult(input.DvarName, true);
            }

            var errorDetail = result.Result?.Errors?.FirstOrDefault()?.Message
                ?? result.Result?.Errors?.FirstOrDefault()?.Code
                ?? "RCON API returned failure";

            logger.LogWarning("RCON API returned failure for {DvarName}: {Error}", input.DvarName, errorDetail);

            return new MapOperationResult(input.DvarName, false, errorDetail);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set RCON dvar {DvarName} on server {GameServerId}",
                input.DvarName, input.GameServerId);
            return new MapOperationResult(input.DvarName, false, ex.Message);
        }
    }
}
