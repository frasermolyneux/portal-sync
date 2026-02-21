using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Net;

using MX.InvisionCommunity.Api.Abstractions;
using MX.InvisionCommunity.Api.Abstractions.Models;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App;

public class UserProfileForumsSync(
    ILogger<UserProfileForumsSync> logger,
    IRepositoryApiClient repositoryApiClient,
    IInvisionApiClient invisionApiClient,
    TelemetryClient telemetryClient)
{
    private const int TakeEntries = 20;

    [Function(nameof(RunUserProfileForumsSyncManual))]
    public async Task RunUserProfileForumsSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await RunUserProfileForumsSync(null).ConfigureAwait(false);
    }

    [Function(nameof(RunUserProfileForumsSync))]
    public async Task RunUserProfileForumsSync([TimerTrigger("0 0 */4 * * *")] TimerInfo? myTimer)
    {
        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            telemetryClient,
            nameof(RunUserProfileForumsSync),
            async () =>
            {
                await ProcessUserProfiles().ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task ProcessUserProfiles()
    {
        var skip = 0;
        var userProfileResponseDto = await repositoryApiClient.UserProfiles.V1.GetUserProfiles(null, null, skip, TakeEntries, null).ConfigureAwait(false);

        do
        {
            var items = userProfileResponseDto?.IsSuccess == true ? userProfileResponseDto.Result?.Data?.Items : null;
            if (items is null)
            {
                logger.LogWarning("User profiles response or result is null");
                break;
            }

            foreach (var userProfileDto in items)
            {
                using (logger.BeginScope(userProfileDto.TelemetryProperties))
                {
                    logger.LogInformation($"UserProfileSync for '{userProfileDto.DisplayName}' with XtremeIdiots ID '{userProfileDto.XtremeIdiotsForumId}'");

                    if (!string.IsNullOrWhiteSpace(userProfileDto.XtremeIdiotsForumId))
                    {
                        try
                        {
                            var memberResult = await invisionApiClient.Core.GetMember(userProfileDto.XtremeIdiotsForumId).ConfigureAwait(false);
                            var member = memberResult.Result?.Data;

                            if (member is not null)
                            {
                                var editUserProfileDto = new EditUserProfileDto(userProfileDto.UserProfileId)
                                {
                                    DisplayName = member.Name ?? userProfileDto.DisplayName,
                                    FormattedName = member.FormattedName ?? userProfileDto.FormattedName,
                                    PrimaryGroup = member.PrimaryGroup?.Name ?? userProfileDto.PrimaryGroup,
                                    Email = member.Email ?? userProfileDto.Email,
                                    PhotoUrl = member.PhotoUrl ?? userProfileDto.PhotoUrl,
                                    ProfileUrl = member.ProfileUrl?.ToString() ?? userProfileDto.ProfileUrl,
                                    TimeZone = member.TimeZone ?? userProfileDto.TimeZone
                                };

                                await repositoryApiClient.UserProfiles.V1.UpdateUserProfile(editUserProfileDto).ConfigureAwait(false);

                                List<CreateUserProfileClaimDto> nonSystemGeneratedClaims = [..userProfileDto.UserProfileClaims
                                    .Where(upc => !upc.SystemGenerated).Select(upc => new CreateUserProfileClaimDto(userProfileDto.UserProfileId, upc.ClaimType, upc.ClaimValue, upc.SystemGenerated))];

                                var activeClaims = GetClaimsForMember(userProfileDto.UserProfileId, member);
                                List<CreateUserProfileClaimDto> claimsToSave = [.. activeClaims, .. nonSystemGeneratedClaims];

                                await repositoryApiClient.UserProfiles.V1.SetUserProfileClaims(userProfileDto.UserProfileId, claimsToSave).ConfigureAwait(false);
                            }
                            else
                            {
                                await repositoryApiClient.UserProfiles.V1.SetUserProfileClaims(userProfileDto.UserProfileId, []).ConfigureAwait(false);
                            }
                        }
                        catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            logger.LogError(httpEx, "Unauthorized access to forums API - invalid API key. Failing fast.");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Failed to sync forum profile for {userProfileDto.XtremeIdiotsForumId}");
                            throw;
                        }

                    }
                }
            }
            skip += TakeEntries;
            userProfileResponseDto = await repositoryApiClient.UserProfiles.V1.GetUserProfiles(null, null, skip, TakeEntries, null).ConfigureAwait(false);
        } while (userProfileResponseDto?.IsSuccess == true && userProfileResponseDto.Result?.Data?.Items?.Any() is true);
    }

    private static List<CreateUserProfileClaimDto> GetClaimsForMember(Guid userProfileId, MemberDto member)
    {
        if (member is null)
        {
            return [];
        }

        List<CreateUserProfileClaimDto> claims =
        [
            new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.UserProfileId, userProfileId.ToString(), true),
            new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.XtremeIdiotsId, member.Id.ToString(), true),
            new CreateUserProfileClaimDto(userProfileId, "Email", member.Email ?? string.Empty, true),
            new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.PhotoUrl, member.PhotoUrl ?? string.Empty, true),
            new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.TimeZone, member.TimeZone ?? string.Empty, true)
        ];

        // Check if PrimaryGroup is not null before trying to use it
        if (member.PrimaryGroup is not null)
        {
            claims.AddRange(GetClaimsForGroup(userProfileId, member.PrimaryGroup));
        }

        // Check if SecondaryGroups is not null before trying to use it
        if (member.SecondaryGroups is not null)
        {
            foreach (var group in member.SecondaryGroups)
            {
                claims.AddRange(GetClaimsForGroup(userProfileId, group ?? new GroupDto()));
            }
        }

        return claims;
    }

    private static List<CreateUserProfileClaimDto> GetClaimsForGroup(Guid userProfileId, GroupDto group)
    {
        List<CreateUserProfileClaimDto> claims = [];

        // Check if group or group.Name is null
        if (group?.Name == null)
        {
            return claims;
        }

        var groupName = group.Name.Replace("+", "").Trim();
        switch (groupName)
        {
            // Senior Admin
            case "Senior Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.SeniorAdmin, GameType.Unknown.ToString(), true));
                break;

            // COD2
            case "COD2 Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.CallOfDuty2.ToString(), true));
                break;
            case "COD2 Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.CallOfDuty2.ToString(), true));
                break;
            case "COD2 Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.CallOfDuty2.ToString(), true));
                break;

            //COD4
            case "COD4 Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.CallOfDuty4.ToString(), true));
                break;
            case "COD4 Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.CallOfDuty4.ToString(), true));
                break;
            case "COD4 Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.CallOfDuty4.ToString(), true));
                break;

            //COD5
            case "COD5 Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.CallOfDuty5.ToString(), true));
                break;
            case "COD5 Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.CallOfDuty5.ToString(), true));
                break;
            case "COD5 Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.CallOfDuty5.ToString(), true));
                break;

            //Insurgency
            case "Insurgency Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Insurgency.ToString(), true));
                break;
            case "Insurgency Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Insurgency.ToString(), true));
                break;
            case "Insurgency Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Insurgency.ToString(), true));
                break;

            //Minecraft
            case "Minecraft Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Minecraft.ToString(), true));
                break;
            case "Minecraft Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Minecraft.ToString(), true));
                break;
            case "Minecraft Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Minecraft.ToString(), true));
                break;

            //ARMA
            case "ARMA Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Arma.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Arma2.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Arma3.ToString(), true));
                break;
            case "ARMA Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Arma.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Arma2.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Arma3.ToString(), true));
                break;
            case "ARMA Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Arma.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Arma2.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Arma3.ToString(), true));
                break;

            //Battlefield
            case "Battlefield Head Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Battlefield1.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Battlefield3.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Battlefield4.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.Battlefield5.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.HeadAdmin, GameType.BattlefieldBadCompany2.ToString(), true));
                break;
            case "Battlefield Admin":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Battlefield1.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Battlefield3.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Battlefield4.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.Battlefield5.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.GameAdmin, GameType.BattlefieldBadCompany2.ToString(), true));
                break;
            case "Battlefield Moderator":
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Battlefield1.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Battlefield3.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Battlefield4.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.Battlefield5.ToString(), true));
                claims.Add(new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.Moderator, GameType.BattlefieldBadCompany2.ToString(), true));
                break;
        }

        return claims;
    }
}
