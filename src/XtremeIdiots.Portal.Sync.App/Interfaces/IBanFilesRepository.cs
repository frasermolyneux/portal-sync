using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.Interfaces;

public interface IBanFilesRepository
{
    /// <summary>
    /// Regenerates the central ban file blob for a game type. When the active
    /// ban set is unchanged since the previous regeneration (compared via
    /// SHA-256 of sorted GUIDs), the upload is skipped and the result reports
    /// <see cref="BanFileRegenerationResult.Skipped"/> = true. The
    /// CentralBanFileStatus row is always upserted so the UI sees fresh
    /// "last attempted" timing.
    /// </summary>
    Task<BanFileRegenerationResult> RegenerateBanFileForGame(GameType gameType, CancellationToken cancellationToken = default);

    Task<long> GetBanFileSizeForGame(GameType gameType);
    Task<Stream> GetBanFileForGame(GameType gameType);
}

/// <summary>
/// Result of a single regeneration attempt for one game type. The function
/// trigger uses this to emit telemetry without re-querying the repository.
/// </summary>
public sealed record BanFileRegenerationResult
{
    public required GameType GameType { get; init; }
    public required bool Skipped { get; init; }
    public required string ActiveBanSetHash { get; init; }
    public required int BanSyncLineCount { get; init; }
    public int? TotalLineCount { get; init; }
    public int? ExternalLineCount { get; init; }
    public long? BlobSizeBytes { get; init; }
    public string? BlobETag { get; init; }
    public required int DurationMs { get; init; }
}
