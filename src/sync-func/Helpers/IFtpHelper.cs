namespace XtremeIdiots.Portal.SyncFunc.Helpers
{
    public interface IFtpHelper
    {
        Task<long?> GetFileSize(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties);
        Task<DateTime?> GetLastModified(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties);
        Task<string> GetRemoteFileData(string hostname, int port, string filePath, string username, string password, Dictionary<string, string> telemetryProperties);
        Task UpdateRemoteFileFromStream(string hostname, int port, string filePath, string username, string password, Stream data, Dictionary<string, string> telemetryProperties);
    }
}