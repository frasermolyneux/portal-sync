namespace XtremeIdiots.Portal.Sync.App.Interfaces
{
    public interface IBanFileIngest
    {
        Task IngestBanFileDataForGame(string gameType, string remoteBanFileData);
    }
}