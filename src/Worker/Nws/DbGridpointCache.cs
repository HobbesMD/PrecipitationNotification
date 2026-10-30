using Persistence;

namespace Worker.Nws;

/// <summary>
/// <see cref="IGridpointCache"/> over the <c>locations</c> table's NWS columns.
/// <c>gridpoint_resolved_at</c> comes from <see cref="TimeProvider"/> (UTC).
/// </summary>
internal sealed class DbGridpointCache(PrecipDbContext db, NwsClient nws, TimeProvider clock) : IGridpointCache
{
    public Task<NwsPoint> GetAsync(Location location, CancellationToken ct) =>
        throw new NotImplementedException();

    public Task<NwsPoint> RefreshAsync(Location location, CancellationToken ct) =>
        throw new NotImplementedException();
}
