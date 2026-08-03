using System.Linq.Expressions;

using MX.Api.Abstractions;
using MX.Api.Client.Configuration;

using XtremeIdiots.Portal.Repository.Abstractions.Interfaces.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.Maps;

namespace XtremeIdiots.Portal.Sync.App.Extensions;

/// <summary>
/// Configures the Repository API client consumer-side L1 cache policies for portal-sync.
/// </summary>
/// <remarks>
/// <para>
/// Rolled forward on Repository client <c>4.2.22</c> / <c>MX.Api.Client 2.3.77</c>, which uses
/// <see cref="SharedCacheConfiguration"/> under the covers to scope every expression to its
/// matching typed sub-API. That fixes the fan-out <c>ArgumentException</c> that took portal-sync
/// down during PR #832 rollout (hotfixed in PR #833 by removing all consumer caching).
/// </para>
/// <para>
/// <b>Maps are explicitly excluded from client L1 caching.</b> portal-sync executes
/// read-then-mutate flows against <see cref="IMapsApi"/> within a single job invocation
/// (<c>MapImageSync</c> reads <c>GetMaps(..., MapsFilter.EmptyMapImage, ...)</c> then writes via
/// <see cref="IMapsApi.UpdateMapImage"/>; <c>MapRedirectSync</c> reads <c>GetMaps</c> then writes
/// via <see cref="IMapsApi.CreateMaps"/> / <see cref="IMapsApi.UpdateMaps"/>). Consumer L1 is not
/// evicted by the Repository server-side tag invalidation, so client-side caching of any map
/// surface would produce stale read-after-write bugs. <see cref="IMapsApi.GetMap"/> is also
/// excluded to keep the exclusion uniform: even paths that today only read (e.g.
/// <c>MapRotationActivities</c>) share the same in-process cache, so caching there would leak
/// staleness into the same invocation as the mutating jobs.
/// </para>
/// <para>
/// The following surfaces stay under <c>UseLibraryDefaults()</c> because portal-sync never
/// mutates them:
/// <list type="bullet">
///   <item><description><see cref="IGameServersApi"/> — only <c>GetGameServers</c> is called
///   (from <c>RedirectToGameServerMapSync</c>); portal-sync performs no game-server writes.
///   Library default is a 60-second in-memory TTL.</description></item>
/// </list>
/// All other sub-APIs used by portal-sync (<see cref="IUserProfileApi"/>,
/// <see cref="IMapRotationsApi"/>, <see cref="ICentralBanFileStatusApi"/>,
/// <see cref="IDataMaintenanceApi"/>) either have library defaults that ship as NotCached
/// (user-profile) or ship with no client-side defaults at all — those continue to hit the API
/// directly and rely on Repository server-side caching, which is the correct behaviour for
/// read-then-write flows in a single invocation.
/// </para>
/// <para>
/// <see cref="IAdminActionsApi.GetAdminActions"/> is also read-only from portal-sync
/// (BanFilesRepository regeneration), but the client package ships no library default for it and
/// active-bans data changes on external timelines, so we keep it uncached and rely on server-side
/// caching to keep freshness guarantees inside a single ban-file regeneration cycle.
/// </para>
/// </remarks>
internal static class RepositoryApiCacheConfiguration
{
    public static void ConfigureCachePolicies(CacheBuilder cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        // Enable the vendor-shipped defaults (60s L1 for GameServer reads, NotCached for
        // user-profile / api-info / api-health, and a 10-minute L1 for single-map reads which
        // we override below).
        cache.UseLibraryDefaults();

        // Force the broad map-list read to bypass client-side caching. Portal-sync's
        // MapImageSync and MapRedirectSync do read-then-mutate against this list inside a
        // single invocation, and L1 is not evicted by server-side tag invalidation.
        // Positional arguments are annotated with inline parameter-name comments because
        // expression trees disallow named arguments (CS0853).
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

        // Force single-map reads to bypass client-side caching as well. Even the callers that
        // today only read (MapRotationActivities.ResolveMapNames) share the same in-process
        // cache as the mutating map jobs; caching here would surface stale read-after-write.
        Expression<Func<IMapsApi, Task<ApiResult<MapDto>>>> getMapExpression =
            api => api.GetMap(
                Guid.Empty,
                default);

        cache.NotCached(getMapExpression);
    }
}
