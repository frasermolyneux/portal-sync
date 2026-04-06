using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App;

public class BanFileMonitor(
    ILogger<BanFileMonitor> logger,
    IBanFilesRepository banFilesRepository,
    TelemetryClient telemetryClient)
{
    private readonly ILogger<BanFileMonitor> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBanFilesRepository banFilesRepository = banFilesRepository ?? throw new ArgumentNullException(nameof(banFilesRepository));
    private readonly TelemetryClient telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));

    [Function(nameof(GenerateLatestBansFileManual))]
    public async Task GenerateLatestBansFileManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
    {
        await GenerateLatestBansFile(null).ConfigureAwait(false);
    }

    [Function(nameof(GenerateLatestBansFile))]
    public async Task GenerateLatestBansFile([TimerTrigger("0 */10 * * * *")] TimerInfo? myTimer)
    {
        await ScheduledJobTelemetry.ExecuteWithTelemetry(
            telemetryClient,
            nameof(GenerateLatestBansFile),
            async () =>
            {
                logger.LogDebug($"Start GenerateLatestBansFile @ {DateTime.UtcNow}");

                GameType[] gameTypes = [GameType.CallOfDuty2, GameType.CallOfDuty4, GameType.CallOfDuty5];
                foreach (var gameType in gameTypes)
                    try
                    {
                        await banFilesRepository.RegenerateBanFileForGame(gameType).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to regenerate latest ban file for {game}", gameType);
                    }

                logger.LogDebug($"Stop GenerateLatestBansFile @ {DateTime.UtcNow}");
            }).ConfigureAwait(false);
    }
}