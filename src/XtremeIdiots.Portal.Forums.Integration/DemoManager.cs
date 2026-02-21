using MX.InvisionCommunity.Api.Abstractions;
using XtremeIdiots.Portal.Forums.Integration.Models;

namespace XtremeIdiots.Portal.Forums.Integration;

public class DemoManager(IInvisionApiClient forumsClient) : IDemoManager
{
    private readonly IInvisionApiClient _invisionClient = forumsClient ?? throw new ArgumentNullException(nameof(forumsClient));
    public async Task<DemoManagerClientDto> GetDemoManagerClient()
    {
        var result = await _invisionClient.Downloads.GetDownloadFile(2753).ConfigureAwait(false);

        var downloadFile = result.Result?.Data;
        if (downloadFile == null)
        {
            throw new ApplicationException("Error getting demo manager client metadata from invision website");
        }

        return new DemoManagerClientDto
        {
            Version = downloadFile.Version,
            Description = downloadFile.Description,
            Url = downloadFile.Url,
            Changelog = downloadFile.Changelog
        };
    }
}