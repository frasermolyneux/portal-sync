using Azure.Identity;
using Azure.Storage.Blobs;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
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
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient;
    public async Task RegenerateBanFileForGame(GameType gameType)
    {
        var blobKey = $"{gameType}-bans.txt";

        _logger.LogInformation($"Regenerating ban file for {gameType} using blob key {blobKey}");

        // Add null check to prevent possible null reference
        if (string.IsNullOrEmpty(_options.Value.StorageBlobEndpoint))
            throw new InvalidOperationException("StorageBlobEndpoint is null or empty");

        var blobServiceClient = new BlobServiceClient(new Uri(_options.Value.StorageBlobEndpoint), new DefaultAzureCredential());
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.Value.ContainerName);

        var blobClient = containerClient.GetBlobClient(blobKey);

        var adminActionsApiResponse = await GetActiveBans(gameType).ConfigureAwait(false);

        var externalBansStream = await GetExternalBanFileForGame(gameType).ConfigureAwait(false);
        externalBansStream.Seek(externalBansStream.Length, SeekOrigin.Begin);

        await using (var streamWriter = new StreamWriter(externalBansStream))
        {
            foreach (var adminActionDto in adminActionsApiResponse)
                streamWriter.WriteLine($"{adminActionDto.Player?.Guid} [BANSYNC]-{adminActionDto.Player?.Username}");

            await streamWriter.FlushAsync().ConfigureAwait(false);
            externalBansStream.Seek(0, SeekOrigin.Begin);
            await blobClient.UploadAsync(externalBansStream, true).ConfigureAwait(false);
        }
    }
    public async Task<long> GetBanFileSizeForGame(GameType gameType)
    {
        var blobKey = $"{gameType}-bans.txt";

        _logger.LogInformation($"Retrieving ban file size for {gameType} using blob key {blobKey}");

        // Add null check to prevent possible null reference
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

        _logger.LogInformation($"Retrieving ban file for {gameType} using blob key {blobKey}");

        return await GetFileStream(blobKey).ConfigureAwait(false);
    }

    private async Task<Stream> GetExternalBanFileForGame(GameType gameType)
    {
        var blobKey = $"{gameType}-external.txt";

        _logger.LogInformation($"Retrieving ban file size for {gameType} using blob key {blobKey}");

        return await GetFileStream(blobKey).ConfigureAwait(false);
    }
    private async Task<Stream> GetFileStream(string blobKey)
    {
        // Add null check to prevent possible null reference
        if (string.IsNullOrEmpty(_options.Value.StorageBlobEndpoint))
            throw new InvalidOperationException("StorageBlobEndpoint is null or empty");

        var blobServiceClient = new BlobServiceClient(new Uri(_options.Value.StorageBlobEndpoint), new DefaultAzureCredential());
        var containerClient = blobServiceClient.GetBlobContainerClient(_options.Value.ContainerName);

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

    private async Task<List<AdminActionDto>> GetActiveBans(GameType gameType)
    {
        const int TakeEntries = 500;
        List<AdminActionDto> adminActions = [];

        var skip = 0;
        var adminActionsApiResponse = await repositoryApiClient.AdminActions.V1.GetAdminActions(gameType, null, null, AdminActionFilter.ActiveBans, skip, TakeEntries, AdminActionOrder.CreatedAsc).ConfigureAwait(false);
        do
        {
            // Null check to ensure Result and Entries exist before accessing them
            if (adminActionsApiResponse?.IsSuccess == true && adminActionsApiResponse.Result?.Data?.Items is not null)
            {
                adminActions.AddRange(adminActionsApiResponse.Result.Data.Items);

                skip += TakeEntries;
                adminActionsApiResponse = await repositoryApiClient.AdminActions.V1.GetAdminActions(gameType, null, null, AdminActionFilter.ActiveBans, skip, TakeEntries, AdminActionOrder.CreatedAsc).ConfigureAwait(false);
            }
            else
            {
                // Exit loop if null results are encountered
                break;
            }
        } while (adminActionsApiResponse?.IsSuccess == true && adminActionsApiResponse.Result?.Data?.Items?.Any() is true);

        return adminActions;
    }
}