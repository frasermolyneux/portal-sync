using XtremeIdiots.Portal.Forums.Integration.Models;

namespace XtremeIdiots.Portal.Forums.Integration;

public interface IDemoManager
{
    Task<DemoManagerClientDto> GetDemoManagerClient();
}