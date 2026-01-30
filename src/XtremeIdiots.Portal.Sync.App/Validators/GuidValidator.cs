using System.Text.RegularExpressions;

using XtremeIdiots.Portal.Sync.App.Interfaces;

namespace XtremeIdiots.Portal.Sync.App.Validators;

public class GuidValidator : IGuidValidator
{
    public bool IsValid(string gameType, string guid)
    {
        var regex = gameType switch
        {
            "CallOfDuty2" => @"^([a-z0-9]{4,32})$",
            "CallOfDuty4" => @"^([a-z0-9]{32})$",
            "CallOfDuty5" => @"^([a-z0-9]{4,32})$",
            _ => throw new ArgumentOutOfRangeException(nameof(gameType), "Game type is unsupported")
        };

        return Regex.IsMatch(guid, regex);
    }
}