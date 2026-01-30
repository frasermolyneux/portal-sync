using XtremeIdiots.Portal.Sync.App.Models;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public interface IMapRedirectRepository
{
    Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game);
}