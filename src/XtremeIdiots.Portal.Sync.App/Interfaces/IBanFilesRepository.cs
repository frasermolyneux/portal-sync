using XtremeIdiots.Portal.RepositoryApi.Abstractions.Constants;

namespace XtremeIdiots.Portal.Sync.App.Interfaces
{
    public interface IBanFilesRepository
    {
        Task RegenerateBanFileForGame(GameType gameType);
        Task<long> GetBanFileSizeForGame(GameType gameType);
        Task<Stream> GetBanFileForGame(GameType gameType);
    }
}