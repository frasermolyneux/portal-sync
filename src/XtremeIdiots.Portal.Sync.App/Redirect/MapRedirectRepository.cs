using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using XtremeIdiots.Portal.Sync.App.Models;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public class MapRedirectRepository(
    IOptions<MapRedirectRepositoryOptions> options,
    HttpClient httpClient,
    ILogger<MapRedirectRepository> logger) : IMapRedirectRepository
{
    private const int MaxContentPreviewLength = 200;
    
    private readonly IOptions<MapRedirectRepositoryOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ILogger<MapRedirectRepository> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    public async Task<List<MapRedirectEntry>> GetMapEntriesForGame(string game)
    {
        var apiKey = _options.Value.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogError("API key is not configured for map redirect repository");
            throw new InvalidOperationException("Map redirect API key is not configured");
        }

        var url = $"{_options.Value.MapRedirectBaseUrl}/portal-map-sync.php?game={game}&key={apiKey}";
        _logger.LogInformation("Requesting map entries for game '{Game}' from map redirect API", game);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to make HTTP request to map redirect API for game '{Game}'", game);
            throw new ApplicationException($"Failed to connect to map redirect API for game '{game}': {ex.Message}", ex);
        }

        var statusCode = (int)response.StatusCode;
        _logger.LogInformation("Received HTTP {StatusCode} response from map redirect API for game '{Game}'", statusCode, game);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        
        // Log a preview of the response content for debugging
        _logger.LogDebug("Response content preview for game '{Game}': {ContentPreview}", game, TruncateForLogging(content));

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Map redirect API returned error status {StatusCode} for game '{Game}'. Response preview: {ErrorPreview}", 
                statusCode, game, TruncateForLogging(content));

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
            _logger.LogError(ex, "Failed to deserialize JSON response from map redirect API for game '{Game}'. Response preview: {ContentPreview}", 
                game, TruncateForLogging(content));
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

    private static string TruncateForLogging(string content)
    {
        if (content.Length <= MaxContentPreviewLength)
        {
            return content;
        }

        return content[..MaxContentPreviewLength] + "... (truncated)";
    }
}