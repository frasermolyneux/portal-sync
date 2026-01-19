using System.Diagnostics;

using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.BanFileMonitors;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Helpers;
using XtremeIdiots.Portal.Sync.App.Interfaces;
using XtremeIdiots.Portal.Sync.App.Telemetry;

namespace XtremeIdiots.Portal.Sync.App
{
    public class BanFileMonitor
    {
        private readonly ILogger<BanFileMonitor> logger;
        private readonly IFtpHelper ftpHelper;
        private readonly IBanFileIngest banFileIngest;
        private readonly IBanFilesRepository banFilesRepository;
        private readonly IRepositoryApiClient repositoryApiClient;
        private readonly TelemetryClient telemetryClient;

        public BanFileMonitor(
            ILogger<BanFileMonitor> logger,
            IFtpHelper ftpHelper,
            IBanFileIngest banFileIngest,
            IBanFilesRepository banFilesRepository,
            IRepositoryApiClient repositoryApiClient,
            TelemetryClient telemetryClient)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.ftpHelper = ftpHelper ?? throw new ArgumentNullException(nameof(ftpHelper));
            this.banFileIngest = banFileIngest ?? throw new ArgumentNullException(nameof(banFileIngest));
            this.banFilesRepository = banFilesRepository ?? throw new ArgumentNullException(nameof(banFilesRepository));
            this.repositoryApiClient = repositoryApiClient ?? throw new ArgumentNullException(nameof(repositoryApiClient));
            this.telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        }

        [Function(nameof(ImportLatestBanFilesManual))]
        public async Task ImportLatestBanFilesManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await ImportLatestBanFiles(null);
        }

        [Function(nameof(ImportLatestBanFiles))]
        public async Task ImportLatestBanFiles([TimerTrigger("0 */5 * * * *")] TimerInfo? myTimer)
        {
            await ScheduledJobTelemetry.ExecuteWithTelemetry(
                telemetryClient,
                nameof(ImportLatestBanFiles),
                async () =>
                {
                    var banFileMonitorsApiResponse = await repositoryApiClient.BanFileMonitors.V1.GetBanFileMonitors(null, null, null, 0, 50, null);

                    if (!banFileMonitorsApiResponse.IsSuccess || banFileMonitorsApiResponse.Result?.Data?.Items == null)
                    {
                        logger.LogCritical("Failed to retrieve ban file monitors from the repository");
                        throw new ApplicationException("Failed to retrieve ban file monitors from the repository");
                    }

                    await ProcessBanFileMonitors(banFileMonitorsApiResponse.Result.Data.Items);
                });
        }

        private async Task ProcessBanFileMonitors(IEnumerable<BanFileMonitorDto> banFileMonitors)
        {
            foreach (var banFileMonitorDto in banFileMonitors)
            {
                if (banFileMonitorDto.GameServer == null)
                    continue;

                if (string.IsNullOrWhiteSpace(banFileMonitorDto.GameServer.FtpHostname)
                    || string.IsNullOrWhiteSpace(banFileMonitorDto.GameServer.FtpUsername)
                    || string.IsNullOrWhiteSpace(banFileMonitorDto.GameServer.FtpPassword)
                    || banFileMonitorDto.GameServer.FtpPort == null)
                    continue;

                try
                {
                    var remoteFileSize = await ftpHelper.GetFileSize(
                    banFileMonitorDto.GameServer.FtpHostname,
                    banFileMonitorDto.GameServer.FtpPort.Value,
                    banFileMonitorDto.FilePath,
                    banFileMonitorDto.GameServer.FtpUsername,
                    banFileMonitorDto.GameServer.FtpPassword,
                    banFileMonitorDto.TelemetryProperties);
                    var banFileSize = await banFilesRepository.GetBanFileSizeForGame(banFileMonitorDto.GameServer.GameType);

                    if (remoteFileSize == null)
                    {
                        telemetryClient.TrackEvent("BanFileInit", banFileMonitorDto.TelemetryProperties);

                        var banFileStream = await banFilesRepository.GetBanFileForGame(banFileMonitorDto.GameServer.GameType);

                        await ftpHelper.UpdateRemoteFileFromStream(
                            banFileMonitorDto.GameServer.FtpHostname,
                            banFileMonitorDto.GameServer.FtpPort.Value,
                            banFileMonitorDto.FilePath,
                            banFileMonitorDto.GameServer.FtpUsername,
                            banFileMonitorDto.GameServer.FtpPassword,
                            banFileStream,
                            banFileMonitorDto.TelemetryProperties);

                        var editBanFileMonitorDto = new EditBanFileMonitorDto(banFileMonitorDto.BanFileMonitorId, banFileSize, DateTime.UtcNow);
                        await repositoryApiClient.BanFileMonitors.V1.UpdateBanFileMonitor(editBanFileMonitorDto);
                        continue;
                    }

                    if (remoteFileSize != banFileMonitorDto.RemoteFileSize)
                    {
                        telemetryClient.TrackEvent("BanFileChangedOnRemote", banFileMonitorDto.TelemetryProperties);

                        var remoteBanFileData = await ftpHelper.GetRemoteFileData(
                            banFileMonitorDto.GameServer.FtpHostname,
                            banFileMonitorDto.GameServer.FtpPort.Value,
                            banFileMonitorDto.FilePath,
                            banFileMonitorDto.GameServer.FtpUsername,
                            banFileMonitorDto.GameServer.FtpPassword,
                            banFileMonitorDto.TelemetryProperties);

                        await banFileIngest.IngestBanFileDataForGame(banFileMonitorDto.GameServer.GameType.ToString(), remoteBanFileData);

                        var editBanFileMonitorDto = new EditBanFileMonitorDto(banFileMonitorDto.BanFileMonitorId, (long)remoteFileSize, DateTime.UtcNow);
                        await repositoryApiClient.BanFileMonitors.V1.UpdateBanFileMonitor(editBanFileMonitorDto);
                    }

                    if (remoteFileSize != banFileSize && remoteFileSize == banFileMonitorDto.RemoteFileSize)
                    {
                        telemetryClient.TrackEvent("BanFileChangedOnSource", banFileMonitorDto.TelemetryProperties);

                        var banFileStream = await banFilesRepository.GetBanFileForGame(banFileMonitorDto.GameServer.GameType);

                        await ftpHelper.UpdateRemoteFileFromStream(
                            banFileMonitorDto.GameServer.FtpHostname,
                            banFileMonitorDto.GameServer.FtpPort.Value,
                            banFileMonitorDto.FilePath,
                            banFileMonitorDto.GameServer.FtpUsername,
                            banFileMonitorDto.GameServer.FtpPassword,
                            banFileStream,
                            banFileMonitorDto.TelemetryProperties);

                        var editBanFileMonitorDto = new EditBanFileMonitorDto(banFileMonitorDto.BanFileMonitorId, banFileSize, DateTime.UtcNow);
                        await repositoryApiClient.BanFileMonitors.V1.UpdateBanFileMonitor(editBanFileMonitorDto);
                    }

                    if (remoteFileSize == banFileMonitorDto.RemoteFileSize)
                    {
                        var editBanFileMonitorDto = new EditBanFileMonitorDto(banFileMonitorDto.BanFileMonitorId)
                        {
                            LastSync = DateTime.UtcNow
                        };
                        await repositoryApiClient.BanFileMonitors.V1.UpdateBanFileMonitor(editBanFileMonitorDto);
                    }
                }
                catch (Exception ex)
                {
                    telemetryClient.TrackException(ex, banFileMonitorDto.TelemetryProperties);
                }
            }
        }

        [Function(nameof(GenerateLatestBansFileManual))]
        public async Task GenerateLatestBansFileManual([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req)
        {
            await GenerateLatestBansFile(null);
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

                    foreach (var gameType in new GameType[] { GameType.CallOfDuty2, GameType.CallOfDuty4, GameType.CallOfDuty5 })
                        try
                        {
                            await banFilesRepository.RegenerateBanFileForGame(gameType);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to regenerate latest ban file for {game}", gameType);
                        }

                    logger.LogDebug($"Stop GenerateLatestBansFile @ {DateTime.UtcNow}");
                });
        }
    }
}