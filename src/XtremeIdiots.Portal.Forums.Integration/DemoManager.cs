using XtremeIdiots.InvisionCommunity;
using XtremeIdiots.Portal.Forums.Integration.Models;

namespace XtremeIdiots.Portal.Forums.Integration
{
    public class DemoManager : IDemoManager
    {
        private readonly IInvisionApiClient _invisionClient;

        public DemoManager(IInvisionApiClient forumsClient)
        {
            _invisionClient = forumsClient ?? throw new ArgumentNullException(nameof(forumsClient));
        }

        public async Task<DemoManagerClientDto> GetDemoManagerClient()
        {
            var downloadFile = await _invisionClient.Downloads.GetDownloadFile(2753);

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
}