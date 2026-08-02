using System.Linq.Expressions;

using MX.Api.Abstractions;
using MX.Api.Client.Configuration;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;

namespace XtremeIdiots.Portal.Sync.App.Extensions;

/// <summary>
/// Configures the Repository API client cache policies for portal-sync.
/// </summary>
/// <remarks>
/// <para>
/// portal-sync executes read-then-mutate flows against <see cref="IMapsApi.GetMaps"/> (map image
/// backfill and map-redirect reconciliation write to the same maps whose "empty image" state was
/// just observed). Consumer-side L1 policies are not evicted by the Repository server-side tag
/// invalidation, so we explicitly force the broad map-list read to bypass client-side caching to
/// guarantee freshness inside a single job invocation.
/// </para>
/// <para>
/// Single-map reads through <see cref="IMapsApi.GetMap(Guid, CancellationToken)"/>
/// (used from <c>MapRotationActivities.ResolveMapNames</c> and shared-map discovery) are read-only
/// within their activity and safely retain the library default 10-minute in-process TTL.
/// Game-server reads retain the library default 60-second TTL.
/// </para>
/// </remarks>
internal static class RepositoryApiCacheConfiguration
{
    public static void ConfigureCachePolicies(CacheBuilder cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        cache.UseLibraryDefaults();

        Expression<Func<IMapsApi, Task<ApiResult<CollectionModel<MapDto>>>>> getMapsListExpression =
            api => api.GetMaps(
                null, // gameType
                null, // mapNames
                null, // filter
                null, // filterString
                0,    // skipEntries
                0,    // takeEntries
                null, // order
                default); // cancellationToken

        cache.NotCached(getMapsListExpression);
    }
}
