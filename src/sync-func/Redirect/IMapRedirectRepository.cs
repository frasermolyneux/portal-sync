using XtremeIdiots.Portal.SyncFunc.Models;

namespace XtremeIdiots.Portal.SyncFunc.Redirect
{
    public interface IMapRedirectRepository
    {
        Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game);
    }
}