using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.CentralBanFileStatus;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Configuration;
using XtremeIdiots.Portal.Sync.App.Interfaces;

namespace XtremeIdiots.Portal.Sync.App.Repository;

public class BanFilesRepository(
    ILogger<BanFilesRepository> logger,
    IOptions<BanFilesRepositoryOptions> options,
    IRepositoryApiClient repositoryApiClient) : IBanFilesRepository
{
    private readonly ILogger<BanFilesRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IOptions<BanFilesRepositoryOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));

    public async Task<BanFileRegenerationResult> RegenerateBanFileForGame(GameType gameType, CancellationToken cancellationToken = default)
    {
        var blobKey = $"{gameType}-bans.txt";
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation("Regenerating ban file for {GameType} using blob key {BlobKey}", gameType, blobKey);

        if (string.IsNullOrEmpty(_options.Value.StorageBlobEndpoint))
            throw new InvalidOperationException("StorageBlobEndpoint is null or empty");

        var blobServiceClient = new BlobServiceClient(new Uri(_options.Value.StorageBlobEndpoint), new DefaultAzureCredential());
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.Value.ContainerName);
        var blobClient = containerClient.GetBlobClient(blobKey);

        // Fetch current status row (if any) and current active bans in parallel — both
        // are needed before we can decide whether to upload.
        var existingStatusTask = TryGetExistingStatusAsync(gameType, cancellationToken);
        var activeBansTask = GetActiveBans(gameType, cancellationToken);
        await Task.WhenAll(existingStatusTask, activeBansTask).ConfigureAwait(false);

        var existingStatus = await existingStatusTask.ConfigureAwait(false);
        var activeBans = await activeBansTask.ConfigureAwait(false);

        var activeBanSetHash = ComputeActiveBanSetHash(activeBans);
        var banSyncLineCount = activeBans.Count;

        // Skip-if-unchanged short-circuit. Avoids re-uploading the full 4 MB blob to the
        // shared storage account every 10 minutes when no bans have changed — the
        // dominant cost downstream is each agent then re-pushing it to its game server.
        if (existingStatus is not null
            && string.Equals(existingStatus.ActiveBanSetHash, activeBanSetHash, StringComparison.Ordinal)
            && existingStatus.BlobETag is not null)
        {
            stopwatch.Stop();

            _logger.LogInformation(
                "Skipping regeneration for {GameType}: active ban set unchanged (hash {Hash}, {BanCount} bans). Last regenerated {LastRegen:o}.",
                gameType, activeBanSetHash, banSyncLineCount, existingStatus.BlobLastRegeneratedUtc);

            // Refresh the external-line count if its source blob changed, even on skip.
            var externalLineCount = await RefreshExternalLineCountIfStaleAsync(
                containerClient, gameType, existingStatus, cancellationToken).ConfigureAwait(false);

            await UpsertStatusAsync(new UpsertCentralBanFileStatusDto(gameType)
            {
                // Hash unchanged, but caller still wants to see this attempt happened
                // — clear any prior error and update ExternalLineCount if it moved.
                ActiveBanSetHash = activeBanSetHash,
                LastRegenerationError = string.Empty,
                LastRegenerationDurationMs = (int)stopwatch.ElapsedMilliseconds,
                ExternalLineCount = externalLineCount?.Count,
                ExternalSourceLastModifiedUtc = externalLineCount?.LastModifiedUtc
            }, cancellationToken).ConfigureAwait(false);

            return new BanFileRegenerationResult
            {
                GameType = gameType,
                Skipped = true,
                ActiveBanSetHash = activeBanSetHash,
                BanSyncLineCount = banSyncLineCount,
                TotalLineCount = existingStatus.TotalLineCount,
                ExternalLineCount = externalLineCount?.Count ?? existingStatus.ExternalLineCount,
                BlobSizeBytes = existingStatus.BlobSizeBytes,
                BlobETag = existingStatus.BlobETag,
                DurationMs = (int)stopwatch.ElapsedMilliseconds
            };
        }

        // Full regeneration path: build the combined blob (external seed + DB bans),
        // upload it, count lines, and write CentralBanFileStatus.
        var externalInfo = await GetExternalBanFileInfoAsync(containerClient, gameType, cancellationToken).ConfigureAwait(false);

        await using var combinedStream = new MemoryStream();
        externalInfo.Content.Seek(0, SeekOrigin.Begin);
        await externalInfo.Content.CopyToAsync(combinedStream, cancellationToken).ConfigureAwait(false);

        // External seed may not end on a newline — guarantee one before appending DB bans.
        if (combinedStream.Length > 0)
        {
            combinedStream.Seek(combinedStream.Length - 1, SeekOrigin.Begin);
            var lastByte = (byte)combinedStream.ReadByte();
            if (lastByte != (byte)'\n')
                combinedStream.WriteByte((byte)'\n');
        }

        await using (var streamWriter = new StreamWriter(combinedStream, leaveOpen: true))
        {
            foreach (var adminActionDto in activeBans)
                await streamWriter.WriteLineAsync($"{adminActionDto.Player?.Guid} [BANSYNC]-{adminActionDto.Player?.Username}").ConfigureAwait(false);

            await streamWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        var blobSizeBytes = combinedStream.Length;
        combinedStream.Seek(0, SeekOrigin.Begin);
        var totalLineCount = CountLines(combinedStream);
        combinedStream.Seek(0, SeekOrigin.Begin);

        var uploadResponse = await blobClient.UploadAsync(combinedStream, overwrite: true, cancellationToken).ConfigureAwait(false);
        var blobEtag = uploadResponse.Value.ETag.ToString();

        stopwatch.Stop();

        _logger.LogInformation(
            "Regenerated ban file for {GameType}: {BanSyncCount} DB bans, {ExternalCount} external lines, {TotalCount} total lines, {SizeBytes} bytes, {DurationMs} ms",
            gameType, banSyncLineCount, externalInfo.LineCount, totalLineCount, blobSizeBytes, stopwatch.ElapsedMilliseconds);

        var nowUtc = DateTime.UtcNow;
        await UpsertStatusAsync(new UpsertCentralBanFileStatusDto(gameType)
        {
            BlobLastRegeneratedUtc = nowUtc,
            BlobETag = blobEtag,
            BlobSizeBytes = blobSizeBytes,
            TotalLineCount = totalLineCount,
            BanSyncLineCount = banSyncLineCount,
            ExternalLineCount = externalInfo.LineCount,
            ExternalSourceLastModifiedUtc = externalInfo.LastModifiedUtc,
            LastRegenerationDurationMs = (int)stopwatch.ElapsedMilliseconds,
            LastRegenerationError = string.Empty,
            ActiveBanSetHash = activeBanSetHash
        }, cancellationToken).ConfigureAwait(false);

        return new BanFileRegenerationResult
        {
            GameType = gameType,
            Skipped = false,
            ActiveBanSetHash = activeBanSetHash,
            BanSyncLineCount = banSyncLineCount,
            TotalLineCount = totalLineCount,
            ExternalLineCount = externalInfo.LineCount,
            BlobSizeBytes = blobSizeBytes,
            BlobETag = blobEtag,
            DurationMs = (int)stopwatch.ElapsedMilliseconds
        };
    }

    public async Task<long> GetBanFileSizeForGame(GameType gameType)
    {
        var blobKey = $"{gameType}-bans.txt";

        _logger.LogInformation("Retrieving ban file size for {GameType} using blob key {BlobKey}", gameType, blobKey);

        if (string.IsNullOrEmpty(_options.Value.StorageBlobEndpoint))
            throw new InvalidOperationException("StorageBlobEndpoint is null or empty");

        var blobServiceClient = new BlobServiceClient(new Uri(_options.Value.StorageBlobEndpoint), new DefaultAzureCredential());
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.Value.ContainerName);

        var blobClient = containerClient.GetBlobClient(blobKey);

        var existsResponse = await blobClient.ExistsAsync().ConfigureAwait(false);
        if (existsResponse.Value)
        {
            var properties = await blobClient.GetPropertiesAsync().ConfigureAwait(false);
            return properties.Value.ContentLength;
        }

        return 0;
    }

    public async Task<Stream> GetBanFileForGame(GameType gameType)
    {
        var blobKey = $"{gameType}-bans.txt";

        _logger.LogInformation("Retrieving ban file for {GameType} using blob key {BlobKey}", gameType, blobKey);

        if (string.IsNullOrEmpty(_options.Value.StorageBlobEndpoint))
            throw new InvalidOperationException("StorageBlobEndpoint is null or empty");

        var blobServiceClient = new BlobServiceClient(new Uri(_options.Value.StorageBlobEndpoint), new DefaultAzureCredential());
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.Value.ContainerName);

        return await GetFileStreamAsync(containerClient, blobKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Stable SHA-256 hash of the active ban set, computed over sorted GUIDs joined
    /// with newlines. Hash equality means "no ban has been added or removed since
    /// the previous regeneration" — the trigger to skip the upload.
    /// </summary>
    internal static string ComputeActiveBanSetHash(IReadOnlyList<AdminActionDto> activeBans)
    {
        var guids = activeBans
            .Select(a => a.Player?.Guid ?? string.Empty)
            .Where(g => !string.IsNullOrEmpty(g))
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToArray();

        var joined = string.Join('\n', guids);
        var bytes = Encoding.UTF8.GetBytes(joined);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Reads the external ban file blob (manually-curated PBBans / B3 / external lists),
    /// streams its content, and returns its line count plus blob metadata.
    /// </summary>
    private async Task<ExternalBanFileInfo> GetExternalBanFileInfoAsync(
        BlobContainerClient containerClient, GameType gameType, CancellationToken ct)
    {
        var blobKey = $"{gameType}-external.txt";
        var blobClient = containerClient.GetBlobClient(blobKey);

        try
        {
            var response = await blobClient.DownloadContentAsync(ct).ConfigureAwait(false);
            var content = response.Value.Content.ToArray();
            var stream = new MemoryStream(content, writable: true);
            stream.Position = 0;
            var lineCount = CountLines(stream);
            stream.Position = 0;

            return new ExternalBanFileInfo
            {
                Content = stream,
                LineCount = lineCount,
                LastModifiedUtc = response.Value.Details.LastModified.UtcDateTime
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("External ban file {BlobKey} does not exist; treating as empty", blobKey);
            return new ExternalBanFileInfo
            {
                Content = new MemoryStream(),
                LineCount = 0,
                LastModifiedUtc = null
            };
        }
    }

    /// <summary>
    /// Refreshes <c>ExternalLineCount</c> only when the source blob's <c>LastModified</c>
    /// has advanced since the last status row was written. Avoids re-reading the 3–4 MB
    /// external blob on every skipped regeneration.
    /// </summary>
    private async Task<ExternalLineCountSnapshot?> RefreshExternalLineCountIfStaleAsync(
        BlobContainerClient containerClient,
        GameType gameType,
        CentralBanFileStatusDto existingStatus,
        CancellationToken ct)
    {
        var blobKey = $"{gameType}-external.txt";
        var blobClient = containerClient.GetBlobClient(blobKey);

        try
        {
            var props = await blobClient.GetPropertiesAsync(cancellationToken: ct).ConfigureAwait(false);
            var lastModified = props.Value.LastModified.UtcDateTime;

            if (existingStatus.ExternalSourceLastModifiedUtc.HasValue
                && existingStatus.ExternalSourceLastModifiedUtc.Value >= lastModified)
            {
                // Source unchanged — no need to recount the 3–4 MB blob.
                return null;
            }

            var content = await blobClient.DownloadContentAsync(ct).ConfigureAwait(false);
            using var stream = new MemoryStream(content.Value.Content.ToArray(), writable: false);
            return new ExternalLineCountSnapshot
            {
                Count = CountLines(stream),
                LastModifiedUtc = lastModified
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Treat a missing external blob as "0 lines, never modified" without
            // overwriting an existing recorded count — the regeneration path will
            // catch up on the next non-skipped cycle.
            _logger.LogDebug("External ban file {BlobKey} not found while refreshing count", blobKey);
            return null;
        }
    }

    private async Task<CentralBanFileStatusDto?> TryGetExistingStatusAsync(GameType gameType, CancellationToken ct)
    {
        var response = await repositoryApiClient.CentralBanFileStatus.V1
            .GetCentralBanFileStatus(gameType, ct)
            .ConfigureAwait(false);

        if (response.IsNotFound)
            return null;

        if (!response.IsSuccess || response.Result?.Data is null)
        {
            _logger.LogWarning(
                "Failed to fetch CentralBanFileStatus for {GameType}: status {StatusCode}. Treating as missing — will regenerate unconditionally.",
                gameType, response.StatusCode);
            return null;
        }

        return response.Result.Data;
    }

    private async Task UpsertStatusAsync(UpsertCentralBanFileStatusDto dto, CancellationToken ct)
    {
        try
        {
            var response = await repositoryApiClient.CentralBanFileStatus.V1
                .UpsertCentralBanFileStatus(dto, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to upsert CentralBanFileStatus for {GameType}: status {StatusCode}",
                    dto.GameType, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Status writes are observability — failures must not break the regeneration
            // path itself, so swallow after logging.
            _logger.LogError(ex, "Exception upserting CentralBanFileStatus for {GameType}", dto.GameType);
        }
    }

    /// <summary>
    /// Reports an explicit failure for the given game type so the dashboard can surface
    /// "regeneration failed" without waiting for the next successful cycle.
    /// </summary>
    internal async Task RecordRegenerationFailureAsync(GameType gameType, Exception ex, int durationMs, CancellationToken ct)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        // Keep the recorded message bounded; SQL column is NVARCHAR(MAX) but admins
        // only need a short hint, full detail goes to App Insights via the LogError above.
        if (message.Length > 1000) message = message[..1000];

        await UpsertStatusAsync(new UpsertCentralBanFileStatusDto(gameType)
        {
            LastRegenerationDurationMs = durationMs,
            LastRegenerationError = message
        }, ct).ConfigureAwait(false);
    }

    private async Task<Stream> GetFileStreamAsync(BlobContainerClient containerClient, string blobKey)
    {
        var blobClient = containerClient.GetBlobClient(blobKey);

        var existsResponse = await blobClient.ExistsAsync().ConfigureAwait(false);
        if (existsResponse.Value)
        {
            var stream = new MemoryStream();
            await blobClient.DownloadToAsync(stream).ConfigureAwait(false);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        return new MemoryStream();
    }

    private async Task<List<AdminActionDto>> GetActiveBans(GameType gameType, CancellationToken cancellationToken)
    {
        const int TakeEntries = 500;
        List<AdminActionDto> adminActions = [];

        var skip = 0;
        var adminActionsApiResponse = await repositoryApiClient.AdminActions.V1
            .GetAdminActions(gameType, null, null, AdminActionFilter.ActiveBans, skip, TakeEntries, AdminActionOrder.CreatedAsc, cancellationToken)
            .ConfigureAwait(false);

        do
        {
            if (adminActionsApiResponse?.IsSuccess == true && adminActionsApiResponse.Result?.Data?.Items is not null)
            {
                adminActions.AddRange(adminActionsApiResponse.Result.Data.Items);

                skip += TakeEntries;
                adminActionsApiResponse = await repositoryApiClient.AdminActions.V1
                    .GetAdminActions(gameType, null, null, AdminActionFilter.ActiveBans, skip, TakeEntries, AdminActionOrder.CreatedAsc, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                break;
            }
        } while (adminActionsApiResponse?.IsSuccess == true && adminActionsApiResponse.Result?.Data?.Items?.Any() is true);

        return adminActions;
    }

    /// <summary>
    /// Counts non-empty lines by scanning bytes for <c>\n</c>. Reads in chunks so
    /// the 3–4 MB external blob can be counted without large allocations beyond the
    /// already-loaded stream.
    /// </summary>
    private static int CountLines(Stream stream)
    {
        var startPos = stream.Position;
        try
        {
            stream.Position = 0;
            var count = 0;
            var buffer = new byte[8192];
            int read;
            var trailingHasContent = false;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        count++;
                        trailingHasContent = false;
                    }
                    else if (buffer[i] != (byte)'\r')
                    {
                        trailingHasContent = true;
                    }
                }
            }
            // Count the trailing partial line (no terminating newline) if it has any content.
            if (trailingHasContent) count++;
            return count;
        }
        finally
        {
            stream.Position = startPos;
        }
    }

    private sealed class ExternalBanFileInfo
    {
        public required Stream Content { get; init; }
        public required int LineCount { get; init; }
        public required DateTime? LastModifiedUtc { get; init; }
    }

    private sealed class ExternalLineCountSnapshot
    {
        public required int Count { get; init; }
        public required DateTime LastModifiedUtc { get; init; }
    }
}
