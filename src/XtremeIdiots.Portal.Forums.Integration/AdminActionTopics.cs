using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using MX.InvisionCommunity.Api.Abstractions;
using XtremeIdiots.Portal.Forums.Integration.Extensions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Forums.Integration;

public class AdminActionTopics(ILogger<AdminActionTopics> logger, IInvisionApiClient forumsClient, IConfiguration configuration) : IAdminActionTopics
{
    private readonly IInvisionApiClient _invisionClient = forumsClient ?? throw new ArgumentNullException(nameof(forumsClient));
    private readonly ILogger<AdminActionTopics> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    public async Task<int> CreateTopicForAdminAction(AdminActionType type, GameType gameType, Guid playerId, string username, DateTime created, string text, string? adminId)
    {
        try
        {
            var userId = int.Parse(configuration["XtremeIdiots:Forums:DefaultAdminUserId"] ?? "21145");
            if (!string.IsNullOrEmpty(adminId) && int.TryParse(adminId, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            var forumId = ResolveForumId(type, gameType);

            var postTopicResult = await _invisionClient.Forums.PostTopic(forumId, userId, $"{username} - {type}", PostContent(type, playerId, username, created, text), type.ToString()).ConfigureAwait(false);

            if (postTopicResult.IsSuccess && postTopicResult.Result?.Data != null)
            {
                return postTopicResult.Result.Data.TopicId;
            }

            _logger.LogError("Error creating admin action topic - call to post topic returned null");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating admin action topic");
            return 0;
        }
    }

    public async Task UpdateTopicForAdminAction(int topicId, AdminActionType type, GameType gameType, Guid playerId, string username, DateTime created, string text, string? adminId)
    {
        if (topicId == 0)
            return;

        try
        {
            var userId = int.Parse(configuration["XtremeIdiots:Forums:DefaultAdminUserId"] ?? "21145");
            if (!string.IsNullOrEmpty(adminId) && int.TryParse(adminId, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            await _invisionClient.Forums.UpdateTopic(topicId, userId, PostContent(type, playerId, username, created, text)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating admin action topic {TopicId}", topicId);
            throw;
        }
    }

    private string PostContent(AdminActionType type, Guid playerId, string username, DateTime created, string text)
    {
        var portalBaseUrl = (configuration["XtremeIdiots:PortalBaseUrl"] ?? "https://portal.xtremeidiots.com").TrimEnd('/');
        return $"""
            <p>
               Username: {username}<br>
               Player Link: <a href="{portalBaseUrl}/Players/Details/{playerId}">Portal</a><br>
               {type} Created: {created.ToString(CultureInfo.InvariantCulture)}
            </p>
            <p>
               {text}
            </p>
            <p>
               <small>Do not edit this post directly as it will be overwritten by the Portal. Add comments on posts below or edit the record in the Portal.</small>
            </p>
            """;
    }

    private int ResolveForumId(AdminActionType type, GameType gameType)
    {
        var defaultForumId = int.Parse(configuration["XtremeIdiots:Forums:DefaultForumId"] ?? "28");

        var category = type switch
        {
            AdminActionType.Observation or AdminActionType.Warning or AdminActionType.Kick => "AdminLogs",
            AdminActionType.TempBan or AdminActionType.Ban => "Bans",
            _ => null
        };

        if (category is null)
            return defaultForumId;

        var gameKey = gameType switch
        {
            GameType.Arma or GameType.Arma2 or GameType.Arma3 => "Arma",
            _ => gameType.ToString()
        };

        var configValue = configuration[$"XtremeIdiots:Forums:{category}:{gameKey}"];
        if (configValue is not null && int.TryParse(configValue, out var forumId))
            return forumId;

        // Fallback to hardcoded values from GameTypeExtensions
        return type switch
        {
            AdminActionType.Observation => gameType.ForumIdForObservations(),
            AdminActionType.Warning => gameType.ForumIdForWarnings(),
            AdminActionType.Kick => gameType.ForumIdForKicks(),
            AdminActionType.TempBan => gameType.ForumIdForTempBans(),
            AdminActionType.Ban => gameType.ForumIdForBans(),
            _ => defaultForumId
        };
    }
}
