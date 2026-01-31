using System.Globalization;

using Microsoft.Extensions.Logging;

using XtremeIdiots.InvisionCommunity;
using XtremeIdiots.Portal.Forums.Integration.Extensions;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Forums.Integration;

public class AdminActionTopics(ILogger<AdminActionTopics> logger, IInvisionApiClient forumsClient) : IAdminActionTopics
{
    private readonly IInvisionApiClient _invisionClient = forumsClient ?? throw new ArgumentNullException(nameof(forumsClient));
    private readonly ILogger<AdminActionTopics> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    public async Task<int> CreateTopicForAdminAction(AdminActionType type, GameType gameType, Guid playerId, string username, DateTime created, string text, string? adminId)
    {
        try
        {
            var userId = string.IsNullOrEmpty(adminId) ? 21145 : int.Parse(adminId); // Admin

            var forumId = type switch
            {
                AdminActionType.Observation => gameType.ForumIdForObservations(),
                AdminActionType.Warning => gameType.ForumIdForWarnings(),
                AdminActionType.Kick => gameType.ForumIdForKicks(),
                AdminActionType.TempBan => gameType.ForumIdForTempBans(),
                AdminActionType.Ban => gameType.ForumIdForBans(),
                _ => 28
            };

            var postTopicResult = await _invisionClient.Forums.PostTopic(forumId, userId, $"{username} - {type}", PostContent(type, playerId, username, created, text), type.ToString()).ConfigureAwait(false);

            if (postTopicResult != null)
            {
                return postTopicResult.TopicId;
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
            var userId = string.IsNullOrEmpty(adminId) ? 21145 : int.Parse(adminId); // Admin

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
        return "<p>" +
               $"   Username: {username}<br>" +
               $"   Player Link: <a href=\"https://portal.xtremeidiots.com/Players/Details/{playerId}\">Portal</a><br>" +
               $"   {type} Created: {created.ToString(CultureInfo.InvariantCulture)}" +
               "</p>" +
               "<p>" +
               $"   {text}" +
               "</p>" +
               "<p>" +
               "   <small>Do not edit this post directly as it will be overwritten by the Portal. Add comments on posts below or edit the record in the Portal.</small>" +
               "</p>";
    }
}
