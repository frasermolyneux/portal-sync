using System.Net;

using Microsoft.Extensions.Logging;

using MX.Api.Abstractions;

using XtremeIdiots.Portal.Sync.App.Models;
using XtremeIdiots.Redirect.Api.Abstractions.V1;

namespace XtremeIdiots.Portal.Sync.App.Redirect;

public class MapRedirectRepository(
    IRedirectApiClient client,
    ILogger<MapRedirectRepository> logger) : IMapRedirectRepository
{
    private const int PageSize = 200;
    private const int SnapshotAttempts = 3;

    public async Task<MapRedirectSnapshot> GetSnapshotForGame(string game, CancellationToken cancellationToken = default)
    {
        var canonicalGame = game.Equals("cod4x", StringComparison.OrdinalIgnoreCase) ? "cod4" : game.ToLowerInvariant();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await ReadSnapshot(canonicalGame, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict && attempt < SnapshotAttempts)
            {
                logger.LogWarning("Redirect catalogue changed while reading '{Game}'; discarding the incomplete snapshot and restarting ({Attempt}/{MaximumAttempts})",
                    game, attempt, SnapshotAttempts);
            }
        }
    }

    private async Task<MapRedirectSnapshot> ReadSnapshot(string game, CancellationToken cancellationToken)
    {
        var discovery = RequireData(await client.Discover(cancellationToken).ConfigureAwait(false), "discover the catalogue", cancellationToken);
        if (string.IsNullOrWhiteSpace(discovery.CatalogueVersion) ||
            !Uri.TryCreate(discovery.DirectBaseUrl, UriKind.Absolute, out var directOrigin) ||
            (directOrigin.Scheme != Uri.UriSchemeHttps && directOrigin.Scheme != Uri.UriSchemeHttp) ||
            directOrigin.UserInfo.Length != 0 || directOrigin.Query.Length != 0 || directOrigin.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Redirect discovery returned an invalid version or direct origin.");
        }

        var root = $"redirect/{game}/usermaps";
        var entries = new Dictionary<string, MapEntry>(StringComparer.Ordinal);
        long? totalCount = null;
        for (var page = 1; ; page++)
        {
            var batch = RequireData(await client.Maps(game, page: page, pageSize: PageSize,
                version: discovery.CatalogueVersion, cancellationToken: cancellationToken).ConfigureAwait(false), $"read maps page {page}", cancellationToken);
            if (batch.Maps is null || batch.Maps.Count > PageSize || batch.Page != page || batch.PageSize != PageSize ||
                batch.Version != discovery.CatalogueVersion || batch.TotalCount < 0 ||
                (totalCount.HasValue && totalCount.Value != batch.TotalCount))
            {
                throw new InvalidOperationException("Redirect returned inconsistent map pagination; no partial snapshot can be synchronized.");
            }

            totalCount = batch.TotalCount;
            foreach (var entry in batch.Maps)
            {
                if (entry.Game != game || !IsCanonicalPath(entry.Name) || entry.Name.Contains('/') ||
                    entry.Path != $"{root}/{entry.Name}" || entry.Bytes < 0 || entry.FileCount < 0)
                {
                    throw new InvalidOperationException("Redirect returned a map entry that does not match the requested game.");
                }
                if (!entries.TryAdd(entry.Name, entry))
                {
                    throw new InvalidOperationException("Redirect returned a duplicate map in a version-pinned catalogue.");
                }
            }

            if (entries.Count == totalCount.Value)
            {
                break;
            }
            if (entries.Count > totalCount.Value || batch.Maps.Count == 0)
            {
                throw new InvalidOperationException("Redirect returned an incomplete map catalogue.");
            }
        }

        var files = entries.Keys.ToDictionary(name => name, _ => new List<ContentEntry>(), StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        totalCount = null;
        for (var page = 1; ; page++)
        {
            var batch = RequireData(await client.BrowseTree(root, page: page, pageSize: PageSize,
                version: discovery.CatalogueVersion, cancellationToken: cancellationToken).ConfigureAwait(false), $"read catalogue tree page {page}", cancellationToken);
            if (batch.Entries is null || batch.Entries.Count > PageSize || batch.Page != page || batch.PageSize != PageSize ||
                batch.Version != discovery.CatalogueVersion || batch.TotalCount < 0 ||
                (totalCount.HasValue && totalCount.Value != batch.TotalCount))
            {
                throw new InvalidOperationException("Redirect returned inconsistent tree pagination; no partial snapshot can be synchronized.");
            }

            totalCount = batch.TotalCount;
            foreach (var entry in batch.Entries)
            {
                if (!IsCanonicalPath(entry.Path) || !entry.Path.StartsWith(root + "/", StringComparison.Ordinal) ||
                    entry.Name != entry.Path[(entry.Path.LastIndexOf('/') + 1)..] ||
                    entry.Bytes < 0 || string.IsNullOrWhiteSpace(entry.Version) ||
                    (!entry.IsDirectory && !HasExpectedDirectUrl(entry, directOrigin)) || !paths.Add(entry.Path))
                {
                    throw new InvalidOperationException("Redirect returned an invalid or duplicate entry in the version-pinned tree.");
                }

                var relative = entry.Path[(root.Length + 1)..];
                var separator = relative.IndexOf('/');
                if (separator < 0 && !entry.IsDirectory)
                {
                    continue;
                }
                var mapName = separator < 0 ? relative : relative[..separator];
                if (!files.TryGetValue(mapName, out var mapFiles))
                {
                    throw new InvalidOperationException("Redirect returned a tree entry outside the enumerated map folders.");
                }
                if (separator < 0)
                {
                    folders.Add(mapName);
                }
                else if (!entry.IsDirectory)
                {
                    mapFiles.Add(entry);
                }
            }

            if (paths.Count == totalCount.Value)
            {
                break;
            }
            if (paths.Count > totalCount.Value || batch.Entries.Count == 0)
            {
                throw new InvalidOperationException("Redirect returned an incomplete catalogue tree.");
            }
        }

        var manifests = new List<MapManifest>();
        foreach (var entry in entries.Values)
        {
            var mapFiles = files[entry.Name];
            if (!folders.Contains(entry.Name) || mapFiles.Count != entry.FileCount || mapFiles.Sum(file => file.Bytes) != entry.Bytes)
            {
                throw new InvalidOperationException($"Redirect returned an inconsistent manifest for '{entry.Name}'.");
            }
            manifests.Add(new MapManifest(game, entry.Name, entry.Path, mapFiles, entry.Bytes, discovery.CatalogueVersion));
        }
        logger.LogInformation("Retrieved {MapCount} complete redirect map manifests for '{Game}' at catalogue version '{Version}'",
            manifests.Count, game, discovery.CatalogueVersion);
        return new MapRedirectSnapshot(discovery.DirectBaseUrl, manifests);
    }

    private static bool IsCanonicalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains('\\') && !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment.Length != 0 && segment != "." && segment != "..");

    private static bool HasExpectedDirectUrl(ContentEntry entry, Uri origin) =>
        Uri.TryCreate(entry.DirectUrl, UriKind.Absolute, out var target) &&
        target.Scheme == origin.Scheme && target.Port == origin.Port &&
        target.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase) && target.UserInfo.Length == 0 &&
        target.Query.Length == 0 && target.Fragment.Length == 0 &&
        target.AbsolutePath == new Uri(origin.AbsoluteUri.TrimEnd('/') + "/" +
            string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString))).AbsolutePath;

    private static T RequireData<T>(ApiResult<T> result, string operation, CancellationToken cancellationToken) where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        {
            throw new HttpRequestException($"Redirect API could not {operation} (HTTP {(int)result.StatusCode}).",
                null, result.StatusCode);
        }
        return result.Result?.Data ?? throw new InvalidOperationException($"Redirect API returned no data while attempting to {operation}.");
    }
}
