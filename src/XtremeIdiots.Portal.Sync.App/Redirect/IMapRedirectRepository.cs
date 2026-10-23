using XtremeIdiots.Portal.Sync.App.Models;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public interface IMapRedirectRepository
{
    Task<MapRedirectSnapshot> GetSnapshotForGame(string game, CancellationToken cancellationToken = default);
}
