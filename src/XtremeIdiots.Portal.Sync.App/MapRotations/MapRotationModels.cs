using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.MapRotations;

// Orchestrator inputs
public record SyncOrchestrationInput(Guid AssignmentId);
public record RemoveOrchestrationInput(Guid AssignmentId);
public record ActivateOrchestrationInput(Guid AssignmentId);
public record DeactivateOrchestrationInput(Guid AssignmentId);
public record VerifyOrchestrationInput(Guid AssignmentId);

// Activity inputs
public record SyncMapInput(Guid GameServerId, string MapName);
public record RemoveMapInput(Guid GameServerId, string MapName);
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
public record ResolveMapNamesInput(List<Guid> MapIds);
public record WriteConfigInput(Guid GameServerId, string ConfigFilePath, string ConfigVariableName, string Value);
public record SetRconDvarInput(Guid GameServerId, string DvarName, string Value);

// Progress tracking
public record OrchestrationProgress(
    string Operation,
    int TotalMaps,
    int CompletedMaps,
    List<MapProgress> Maps);

public record MapProgress(string MapName, string Status, string? Error = null);
// Status values: "Pending", "InProgress", "Completed", "Failed", "Skipped"

// Activity outputs
public record MapOperationResult(string MapName, bool Success, string? Error = null);
public record RotationDetails(
    Guid AssignmentId,
    Guid GameServerId,
    Guid MapRotationId,
    DeploymentState DeploymentState,
    ActivationState ActivationState,
    int? DeployedVersion,
    int RotationVersion,
    string? ContentHash,
    string? ConfigFilePath,
    string? ConfigVariableName,
    string? GameMode,
    List<Guid> MapIds);
