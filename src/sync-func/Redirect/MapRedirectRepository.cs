using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using XtremeIdiots.Portal.SyncFunc.Models;

namespace XtremeIdiots.Portal.SyncFunc.Redirect
{
    public class MapRedirectRepository : IMapRedirectRepository
    {
        private readonly IOptions<MapRedirectRepositoryOptions> _options;

        public MapRedirectRepository(IOptions<MapRedirectRepositoryOptions> options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game)
        {
            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetAsync($"{_options.Value.MapRedirectBaseUrl}/portal-map-sync.php?game={game}&key={_options.Value.ApiKey}");
                var content = await response.Content.ReadAsStringAsync();

                var mapRedirectEntries = JsonConvert.DeserializeObject<List<MapRedirectEntry>>(content);

                if (mapRedirectEntries == null)
                {
                    throw new ApplicationException("Failed to retrieve map entries from redirect server");
                }

                return mapRedirectEntries;
            }
        }
    }
}