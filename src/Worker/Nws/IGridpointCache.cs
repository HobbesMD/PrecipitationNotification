using Persistence;

namespace Worker.Nws;

/// <summary>
/// The <c>/points</c> lookup, cached on the <see cref="Location"/> row (CLAUDE.md:
/// "Gridpoint mapping is stable — cache it").
/// </summary>
internal interface IGridpointCache
{
    /// <summary>The cached point, resolving and storing it first if the location has none.</summary>
    Task<NwsPoint> GetAsync(Location location, CancellationToken ct);

    /// <summary>Resolves the point from NWS again and overwrites the cache, e.g. after <see cref="NwsPointStaleException"/>.</summary>
    Task<NwsPoint> RefreshAsync(Location location, CancellationToken ct);
}
