using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.InvisionCommunity;
using XtremeIdiots.InvisionCommunity.Models;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.UserProfiles;
using XtremeIdiots.Portal.Repository.Api.Client.V1;

namespace XtremeIdiots.Portal.Sync.App
{
    public class UserProfileForumsSync
    {
        private const int TakeEntries = 20;
        private readonly ILogger<UserProfileForumsSync> logger;
        private readonly IRepositoryApiClient repositoryApiClient;
        private readonly IInvisionApiClient invisionApiClient;

        public UserProfileForumsSync(
            ILogger<UserProfileForumsSync> logger,
            IRepositoryApiClient repositoryApiClient,
            IInvisionApiClient invisionApiClient)
        {
            this.logger = logger;
            this.repositoryApiClient = repositoryApiClient;
            this.invisionApiClient = invisionApiClient;
        }

        [Function(nameof(RunUserProfileForumsSyncManual))]
        public async Task RunUserProfileForumsSyncManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await RunUserProfileForumsSync(null);
        }

        [Function(nameof(RunUserProfileForumsSync))]
        public async Task RunUserProfileForumsSync([TimerTrigger("0 0 0 * * *")] TimerInfo? myTimer)
        {
            var skip = 0;
            var userProfileResponseDto = await repositoryApiClient.UserProfiles.V1.GetUserProfiles(null, skip, TakeEntries, null);

            // Ensure the response and result are not null
            if (userProfileResponseDto?.Result == null)
            {
                logger.LogWarning("User profiles response or result is null");
                return;
            }

            do
            {
                foreach (var userProfileDto in userProfileResponseDto.Result.Entries)
                {
                    using (logger.BeginScope(userProfileDto.TelemetryProperties))
                    {
                        logger.LogInformation($"UserProfileSync for '{userProfileDto.DisplayName}' with XtremeIdiots ID '{userProfileDto.XtremeIdiotsForumId}'");

                        if (!string.IsNullOrWhiteSpace(userProfileDto.XtremeIdiotsForumId))
                        {
                            try
                            {
                                var member = await invisionApiClient.Core.GetMember(userProfileDto.XtremeIdiotsForumId);

                                if (member != null)
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

                                    await repositoryApiClient.UserProfiles.V1.UpdateUserProfile(editUserProfileDto);

                                    var nonSystemGeneratedClaims = userProfileDto.UserProfileClaims
                                        .Where(upc => !upc.SystemGenerated).Select(upc => new CreateUserProfileClaimDto(userProfileDto.UserProfileId, upc.ClaimType, upc.ClaimValue, upc.SystemGenerated))
                                        .ToList();

                                    var activeClaims = GetClaimsForMember(userProfileDto.UserProfileId, member);
                                    var claimsToSave = activeClaims.Concat(nonSystemGeneratedClaims).ToList();

                                    await repositoryApiClient.UserProfiles.V1.SetUserProfileClaims(userProfileDto.UserProfileId, claimsToSave);
                                }
                                else
                                {
                                    await repositoryApiClient.UserProfiles.V1.SetUserProfileClaims(userProfileDto.UserProfileId, new List<CreateUserProfileClaimDto>());
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, $"Failed to sync forum profile for {userProfileDto.XtremeIdiotsForumId}");
                            }

                        }
                    }
                }

                skip += TakeEntries;
                userProfileResponseDto = await repositoryApiClient.UserProfiles.V1.GetUserProfiles(null, skip, TakeEntries, null);
            } while (userProfileResponseDto?.Result?.Entries != null && userProfileResponseDto.Result.Entries.Count > 0);
        }

        private static List<CreateUserProfileClaimDto> GetClaimsForMember(Guid userProfileId, Member member)
        {
            if (member == null)
            {
                return new List<CreateUserProfileClaimDto>();
            }

            var claims = new List<CreateUserProfileClaimDto>
            {
                new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.UserProfileId, userProfileId.ToString(), true),
                new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.XtremeIdiotsId, member.Id.ToString(), true),
                new CreateUserProfileClaimDto(userProfileId, "Email", member.Email ?? string.Empty, true),
                new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.PhotoUrl, member.PhotoUrl ?? string.Empty, true),
                new CreateUserProfileClaimDto(userProfileId, UserProfileClaimType.TimeZone, member.TimeZone ?? string.Empty, true),
            };

            // Check if PrimaryGroup is not null before trying to use it
            if (member.PrimaryGroup != null)
            {
                claims = claims.Concat(GetClaimsForGroup(userProfileId, member.PrimaryGroup)).ToList();
            }

            // Check if SecondaryGroups is not null before trying to use it
            if (member.SecondaryGroups != null)
            {
                claims = member.SecondaryGroups.Aggregate(claims, (current, group) => current.Concat(GetClaimsForGroup(userProfileId, group ?? new Group())).ToList());
            }

            return claims;
        }

        private static List<CreateUserProfileClaimDto> GetClaimsForGroup(Guid userProfileId, Group group)
        {
            var claims = new List<CreateUserProfileClaimDto>();

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
}
