using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Forums.Integration;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Extensions.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Players;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;

namespace XtremeIdiots.Portal.Sync.App.Ingest;

internal class BanFileIngest(
    ILogger<BanFileIngest> logger,
    IGuidValidator guidValidator,
    IAdminActionTopics adminActionTopics,
    IRepositoryApiClient repositoryApiClient) : IBanFileIngest
{
    private readonly ILogger<BanFileIngest> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGuidValidator _guidValidator = guidValidator ?? throw new ArgumentNullException(nameof(guidValidator));
    private readonly IAdminActionTopics adminActionTopics = adminActionTopics ?? throw new ArgumentNullException(nameof(adminActionTopics));
    private readonly IRepositoryApiClient repositoryApiClient = repositoryApiClient;
    public async Task IngestBanFileDataForGame(string gameType, string remoteBanFileData)
    {
        string[] skipTags = ["[PBBAN]", "[B3BAN]", "[BANSYNC]", "[EXTERNAL]"];
        var gameTypeEnum = Enum.Parse<GameType>(gameType);
        foreach (var line in remoteBanFileData.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line) || skipTags.Any(skipTag => line.Contains(skipTag))) continue;

            ParseLine(line, out var guid, out var name);

            if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(name)) continue;

            if (!_guidValidator.IsValid(gameType, guid))
            {
                _logger.LogWarning($"Could not validate guid {guid} for {gameType}");
                continue;
            }
            var playerDtoApiResponse = await repositoryApiClient.Players.V1.GetPlayerByGameType(gameTypeEnum, guid, PlayerEntityOptions.None);

            if (playerDtoApiResponse.IsNotFound)
            {
                _logger.LogInformation($"BanFileIngest - creating new player {name} with guid {guid} with import ban");

                await repositoryApiClient.Players.V1.CreatePlayer(new CreatePlayerDto(name, guid, gameType.ToGameType()));

                playerDtoApiResponse = await repositoryApiClient.Players.V1.GetPlayerByGameType(gameTypeEnum, guid, PlayerEntityOptions.None);

                if (playerDtoApiResponse == null || playerDtoApiResponse.Result?.Data == null)
                    throw new Exception("Newly created player could not be retrieved from database");

                var createAdminActionDto = new CreateAdminActionDto(playerDtoApiResponse.Result.Data.PlayerId, AdminActionType.Ban, "Imported from server");
                createAdminActionDto.ForumTopicId = await adminActionTopics.CreateTopicForAdminAction(createAdminActionDto.Type, playerDtoApiResponse.Result.Data.GameType, playerDtoApiResponse.Result.Data.PlayerId, playerDtoApiResponse.Result.Data.Username, DateTime.UtcNow, createAdminActionDto.Text, createAdminActionDto.AdminId);

                await repositoryApiClient.AdminActions.V1.CreateAdminAction(createAdminActionDto);
            }
            else if (playerDtoApiResponse.Result?.Data != null)
            {
                var adminActionsApiResponse = await repositoryApiClient.AdminActions.V1.GetAdminActions(null, playerDtoApiResponse.Result.Data.PlayerId, null, AdminActionFilter.ActiveBans, 0, 500, null);

                if (adminActionsApiResponse == null || adminActionsApiResponse.Result == null)
                    throw new Exception("Failed to retieve admin actions for player from database");

                if (adminActionsApiResponse.Result?.Data?.Items?.Count(aa => aa.Type == AdminActionType.Ban) == 0)
                {
                    _logger.LogInformation($"BanFileImport - adding import ban to existing player {playerDtoApiResponse.Result.Data.Username} - {playerDtoApiResponse.Result.Data.Guid} ({playerDtoApiResponse.Result.Data.GameType})");

                    var createAdminActionDto = new CreateAdminActionDto(playerDtoApiResponse.Result.Data.PlayerId, AdminActionType.Ban, "Imported from server");
                    createAdminActionDto.ForumTopicId = await adminActionTopics.CreateTopicForAdminAction(createAdminActionDto.Type, playerDtoApiResponse.Result.Data.GameType, playerDtoApiResponse.Result.Data.PlayerId, playerDtoApiResponse.Result.Data.Username, DateTime.UtcNow, createAdminActionDto.Text, createAdminActionDto.AdminId);

                    await repositoryApiClient.AdminActions.V1.CreateAdminAction(createAdminActionDto);
                }
            }

        }
    }

    private void ParseLine(string line, out string? guid, out string? name)
    {
        try
        {
            var trimmedLine = line.Trim();
            var indexOfSpace = trimmedLine.IndexOf(' ');

            guid = trimmedLine[..indexOfSpace].Trim().ToLower();
            name = trimmedLine[indexOfSpace..].Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to parse {line} when ingesting ban file");
            guid = name = null;
        }
    }
}