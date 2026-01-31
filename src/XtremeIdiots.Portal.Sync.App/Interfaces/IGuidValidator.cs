namespace XtremeIdiots.Portal.Sync.App.Interfaces;

public interface IGuidValidator
{
    bool IsValid(string gameType, string guid);
}