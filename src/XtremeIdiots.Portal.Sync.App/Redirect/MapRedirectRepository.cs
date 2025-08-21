using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Sync.App.Models;

namespace XtremeIdiots.Portal.Sync.App.Redirect
{
    public class MapRedirectRepository : IMapRedirectRepository
    {
        private readonly IOptions<MapRedirectRepositoryOptions> _options;
        private readonly HttpClient _httpClient;

        public MapRedirectRepository(IOptions<MapRedirectRepositoryOptions> options, HttpClient httpClient)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game)
        {
            var response = await _httpClient.GetAsync($"{_options.Value.MapRedirectBaseUrl}/portal-map-sync.php?game={game}&key={_options.Value.ApiKey}");
            response.EnsureSuccessStatusCode();
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