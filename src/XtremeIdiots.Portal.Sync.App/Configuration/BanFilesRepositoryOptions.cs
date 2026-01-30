namespace XtremeIdiots.Portal.Sync.App.Configuration;

public class BanFilesRepositoryOptions
{
    public string? StorageBlobEndpoint { get; set; }
    public string? ContainerName { get; set; } = "ban-files";
}