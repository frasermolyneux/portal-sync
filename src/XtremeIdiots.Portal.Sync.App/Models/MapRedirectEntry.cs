﻿namespace XtremeIdiots.Portal.Sync.App.Models
{
    public class MapRedirectEntry
    {
        public string MapName { get; set; } = string.Empty;
        public List<string> MapFiles { get; set; } = new List<string>();
    }
}