using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;

namespace XtremeIdiots.Portal.Sync.App.Interfaces;

public interface IBanFilesRepository
{
    Task RegenerateBanFileForGame(GameType gameType);
    Task<long> GetBanFileSizeForGame(GameType gameType);
    Task<Stream> GetBanFileForGame(GameType gameType);
}