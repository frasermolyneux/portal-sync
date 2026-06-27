using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Repository;

namespace XtremeIdiots.Portal.Sync.App;

public class BanFileMonitor(
    ILogger<BanFileMonitor> logger,
    IBanFilesRepository banFilesRepository,
    IJobTelemetry jobTelemetry)
{
    private readonly ILogger<BanFileMonitor> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBanFilesRepository banFilesRepository = banFilesRepository ?? throw new ArgumentNullException(nameof(banFilesRepository));
    private readonly IJobTelemetry jobTelemetry = jobTelemetry ?? throw new ArgumentNullException(nameof(jobTelemetry));

    [Function(nameof(GenerateLatestBansFileManual))]
    public async Task GenerateLatestBansFileManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await GenerateLatestBansFile(null).ConfigureAwait(false);
    }

    [Function(nameof(GenerateLatestBansFile))]
    public async Task GenerateLatestBansFile([TimerTrigger("0 */10 * * * *")] TimerInfo? myTimer)
    {
        await jobTelemetry.ExecuteAsync(
            nameof(GenerateLatestBansFile),
            async () =>
            {
                logger.LogDebug("Start GenerateLatestBansFile @ {Now:o}", DateTime.UtcNow);

                // CoD4x emits the cod4x simplebanlist v2 format (banlist_v2.dat) instead of the
                // legacy ban.txt — the format branch lives inside BanFilesRepository.
                GameType[] gameTypes = [GameType.CallOfDuty2, GameType.CallOfDuty4, GameType.CallOfDuty5, GameType.CallOfDuty4x];
                foreach (var gameType in gameTypes)
                {
                    try
                    {
                        _ = await banFilesRepository.RegenerateBanFileForGame(gameType).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to regenerate latest ban file for {Game}", gameType);

                        // Surface the failure on the dashboard via CentralBanFileStatus.LastRegenerationError
                        // so admins do not need App Insights to spot a stuck game type.
                        if (banFilesRepository is BanFilesRepository concrete)
                        {
                            try
                            {
                                await concrete.RecordRegenerationFailureAsync(gameType, ex, durationMs: 0, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch (Exception statusEx)
                            {
                                logger.LogWarning(statusEx, "Failed to record regeneration failure status for {Game}", gameType);
                            }
                        }
                    }
                }

                logger.LogDebug("Stop GenerateLatestBansFile @ {Now:o}", DateTime.UtcNow);
            }).ConfigureAwait(false);
    }
}
