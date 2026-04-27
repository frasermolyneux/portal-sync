using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using MX.Observability.ApplicationInsights.Auditing;
using MX.Observability.ApplicationInsights.Auditing.Models;
using MX.Observability.ApplicationInsights.Jobs;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Repository;

namespace XtremeIdiots.Portal.Sync.App;

public class BanFileMonitor(
    ILogger<BanFileMonitor> logger,
    IBanFilesRepository banFilesRepository,
    IJobTelemetry jobTelemetry,
    IAuditLogger auditLogger)
{
    private readonly ILogger<BanFileMonitor> logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IBanFilesRepository banFilesRepository = banFilesRepository ?? throw new ArgumentNullException(nameof(banFilesRepository));
    private readonly IJobTelemetry jobTelemetry = jobTelemetry ?? throw new ArgumentNullException(nameof(jobTelemetry));
    private readonly IAuditLogger auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));

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

                GameType[] gameTypes = [GameType.CallOfDuty2, GameType.CallOfDuty4, GameType.CallOfDuty5];
                foreach (var gameType in gameTypes)
                {
                    try
                    {
                        var result = await banFilesRepository.RegenerateBanFileForGame(gameType).ConfigureAwait(false);

                        // Audit either Skipped or Generated so admins can correlate why the
                        // central blob ETag did or did not advance for this cycle.
                        var actionName = result.Skipped ? "BanFileRegenerationSkipped" : "BanFileGenerated";
                        auditLogger.LogAudit(AuditEvent.SystemAction(actionName, AuditAction.Export)
                            .WithService("BanFileMonitor")
                            .WithProperty("GameType", gameType.ToString())
                            .WithProperty("Skipped", result.Skipped.ToString())
                            .WithProperty("ActiveBanSetHash", result.ActiveBanSetHash)
                            .WithProperty("BanSyncLineCount", result.BanSyncLineCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .WithProperty("DurationMs", result.DurationMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
                            .WithSource("BanFileMonitor")
                            .Build());
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
