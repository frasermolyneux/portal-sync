using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

// Orchestrator inputs
public record SyncOrchestrationInput(Guid AssignmentId);
public record RemoveOrchestrationInput(Guid AssignmentId);
public record ActivateOrchestrationInput(Guid AssignmentId);
public record DeactivateOrchestrationInput(Guid AssignmentId);
public record VerifyOrchestrationInput(Guid AssignmentId);
public record PushMapOrchestrationInput(Guid GameServerId, string MapName, GameType GameType);

// Activity inputs
public record SyncMapInput(Guid GameServerId, string MapName, GameType GameType);
public record RemoveMapInput(Guid GameServerId, string MapName, GameType GameType);
public record GetLoadedMapsInput(Guid GameServerId);
public record GetSharedMapsInput(Guid GameServerId, Guid ExcludeAssignmentId);
public record UpdateStatusInput(
    Guid AssignmentId,
    DeploymentState? DeploymentState = null,
    ActivationState? ActivationState = null,
    int? DeployedVersion = null,
    int? ActivatedVersion = null,
    string? LastError = null,
    DateTime? LastErrorAt = null,
    DateTime? UnassignedAt = null);
public record RecordOperationInput(Guid AssignmentId, AssignmentOperationType OperationType, string? DurableFunctionInstanceId = null);
public record CompleteOperationInput(Guid OperationId, AssignmentOperationStatus Status, string? Error = null);
public record GetRotationDetailsInput(Guid AssignmentId);
public record FormatRotationInput(List<string> MapNames, string GameMode, string ConfigVariableName);
public record FormatRotationOutput(List<RotationStringPart> Parts);
public record RotationStringPart(string VariableName, string Value);
public record ResolveMapNamesInput(List<Guid> MapIds);
public record WriteConfigInput(Guid GameServerId, string ConfigFilePath, string ConfigVariableName, string Value, string[]? CommentLines = null);
public record SetRconDvarInput(Guid GameServerId, string DvarName, string Value);

public static class RotationVariableNaming
{
    /// <summary>
    /// Parses a variable name into its root prefix and starting numeric suffix.
    /// E.g., "scr_aacp_maps_2" → ("scr_aacp_maps_", 2), "sv_maprotation" → ("sv_maprotation_", 1)
    /// </summary>
    public static (string Root, int StartIndex) ParseVariableNameBase(string variableName)
    {
        var i = variableName.Length - 1;
        while (i >= 0 && char.IsDigit(variableName[i]))
        {
            i--;
        }

        if (i < variableName.Length - 1 && int.TryParse(variableName.AsSpan(i + 1), out var startIndex))
        {
            return (variableName[..(i + 1)], startIndex);
        }

        return (variableName + "_", 1);
    }

    /// <summary>
    /// Generates overflow variable names for cleanup, starting after the last used index.
    /// </summary>
    public static IEnumerable<string> GetOverflowVariableNames(string baseVariable, HashSet<string> usedNames, int maxOverflow = 9)
    {
        var (root, startIndex) = ParseVariableNameBase(baseVariable);

        for (var idx = startIndex; idx <= startIndex + maxOverflow; idx++)
        {
            var varName = $"{root}{idx}";

            if (!usedNames.Contains(varName))
            {
                yield return varName;
            }
        }
    }
}

// Progress tracking
public record OrchestrationProgress(
    string Operation,
    int TotalMaps,
    int CompletedMaps,
    List<MapProgress> Maps);

public record MapProgress(string MapName, string Status, string? Error = null);
// Status values: "Pending", "InProgress", "Completed", "Failed", "Skipped"

// Activity outputs
public record MapOperationResult(string MapName, bool Success, string? Error = null, string? SkipReason = null);

public static class SkipReasons
{
    public const string BuiltInMap = "Built-in map";
    public const string NoMapFiles = "No map files available";
    public const string AacpSharedMaps = "AACP — maps shared with main rotation";
}
public record RotationDetails(
    Guid AssignmentId,
    Guid GameServerId,
    Guid MapRotationId,
    GameType GameType,
    DeploymentState DeploymentState,
    ActivationState ActivationState,
    int? DeployedVersion,
    int RotationVersion,
    string? ContentHash,
    string? ConfigFilePath,
    string? ConfigVariableName,
    string? GameMode,
    List<Guid> MapIds,
    string? Title = null);
