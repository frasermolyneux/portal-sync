using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Sync.App.Models;

namespace XtremeIdiots.Portal.Sync.App.Redirect
{
    public class MapRedirectRepository : IMapRedirectRepository
    {
        private readonly IOptions<MapRedirectRepositoryOptions> _options;
        private readonly HttpClient _httpClient;
        private readonly ILogger<MapRedirectRepository> _logger;

        public MapRedirectRepository(IOptions<MapRedirectRepositoryOptions> options, HttpClient httpClient, ILogger<MapRedirectRepository> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game)
        {
            var apiKey = _options.Value.ApiKey ?? string.Empty;
            var url = $"{_options.Value.MapRedirectBaseUrl}/portal-map-sync.php?game={game}&key={apiKey}";
            var maskedUrl = url.Replace(apiKey, "***");
            _logger.LogInformation("Requesting map entries for game '{Game}' from map redirect API", game);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to make HTTP request to map redirect API for game '{Game}'", game);
                throw new ApplicationException($"Failed to connect to map redirect API for game '{game}': {ex.Message}", ex);
            }

            var statusCode = (int)response.StatusCode;
            _logger.LogInformation("Received HTTP {StatusCode} response from map redirect API for game '{Game}'", statusCode, game);

            var content = await response.Content.ReadAsStringAsync();
            
            // Log a preview of the response content for debugging (truncate if too long)
            var contentPreview = content.Length > 200 ? content.Substring(0, 200) + "..." : content;
            _logger.LogDebug("Response content preview for game '{Game}': {ContentPreview}", game, contentPreview);

            if (!response.IsSuccessStatusCode)
            {
                // Truncate error response for logging to avoid exposing sensitive data
                var errorPreview = content.Length > 200 ? content.Substring(0, 200) + "... (truncated)" : content;
                _logger.LogError("Map redirect API returned error status {StatusCode} for game '{Game}'. Response preview: {ErrorPreview}", 
                    statusCode, game, errorPreview);

                if (statusCode == 401 || statusCode == 403)
                {
                    throw new ApplicationException($"Authentication failed for map redirect API (HTTP {statusCode}). Check that the API key is valid.");
                }

                throw new ApplicationException($"Map redirect API returned error status {statusCode} for game '{game}'.");
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogError("Map redirect API returned empty response for game '{Game}'", game);
                throw new ApplicationException($"Map redirect API returned empty response for game '{game}'");
            }

            List<MapRedirectEntry>? mapRedirectEntries;
            try
            {
                mapRedirectEntries = JsonConvert.DeserializeObject<List<MapRedirectEntry>>(content);
            }
            catch (JsonException ex)
            {
                // Log truncated content to avoid exposing sensitive data
                var contentForLogging = content.Length > 200 ? content.Substring(0, 200) + "... (truncated)" : content;
                _logger.LogError(ex, "Failed to deserialize JSON response from map redirect API for game '{Game}'. Response preview: {ContentPreview}", 
                    game, contentForLogging);
                throw new ApplicationException($"Failed to parse JSON response from map redirect API for game '{game}'. This may indicate an invalid API key or server error.", ex);
            }

            if (mapRedirectEntries == null)
            {
                _logger.LogError("Deserialized map entries is null for game '{Game}'", game);
                throw new ApplicationException($"Failed to retrieve map entries from redirect server for game '{game}'. Response deserialized to null.");
            }

            _logger.LogInformation("Successfully retrieved {Count} map entries for game '{Game}'", mapRedirectEntries.Count, game);
            return mapRedirectEntries;
        }
    }
}