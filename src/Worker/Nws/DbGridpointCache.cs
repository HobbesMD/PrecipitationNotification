using Microsoft.EntityFrameworkCore;
using Persistence;

namespace Worker.Nws;

/// <summary>
/// <see cref="IGridpointCache"/> over the <c>locations</c> table's NWS columns.
/// <c>gridpoint_resolved_at</c> comes from <see cref="TimeProvider"/> (UTC).
/// </summary>
internal sealed class DbGridpointCache(PrecipDbContext db, NwsClient nws, TimeProvider clock) : IGridpointCache
{
    public Task<NwsPoint> GetAsync(Location location, CancellationToken ct) =>
        location is { TimeZoneId: { } zone, ForecastHourlyUrl: { } hourly }
            ? Task.FromResult(new NwsPoint(zone, hourly, location.ForecastGridDataUrl))
            : RefreshAsync(location, ct);

    public async Task<NwsPoint> RefreshAsync(Location location, CancellationToken ct)
    {
        // The coordinates are secret (D22): they go into the request path and nowhere else.
        var point = await nws.ResolvePointAsync(
            NwsCoordinates.Format(location.Latitude, location.Longitude), ct);

        // The caller may have loaded the location through a different context.
        if (db.Entry(location).State == EntityState.Detached)
            db.Attach(location);

        location.TimeZoneId = point.TimeZoneId;
        location.ForecastHourlyUrl = point.ForecastHourlyUrl;
        location.ForecastGridDataUrl = point.ForecastGridDataUrl;
        location.GridpointResolvedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        return point;
    }
}
