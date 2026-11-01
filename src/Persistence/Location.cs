namespace Persistence;

/// <summary>
/// A place to watch. Coordinates are <see cref="decimal"/> stored as
/// <c>numeric(6,4)</c>/<c>numeric(7,4)</c>, not <c>double</c>: NWS accepts at most
/// four decimal places, and an exact type means what's stored is exactly what's
/// sent, with the database refusing a fifth digit rather than rounding it away.
///
/// Coordinates pin a home (D22): never log them or put them in an exception message.
///
/// The NWS columns cache the <c>/points</c> lookup. The gridpoint mapping is stable,
/// and a container has no disk to cache it on, so it lives on the row. They are
/// null until the first resolve; <see cref="GridpointResolvedAt"/> says when that was.
/// </summary>
public sealed class Location
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public decimal Latitude { get; set; }

    public decimal Longitude { get; set; }

    /// <summary>Defaults to true in the database too, so a raw-SQL insert gets the same answer.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>IANA zone id from NWS (D10): the window is local to the location, not to config.</summary>
    public string? TimeZoneId { get; set; }

    public string? ForecastHourlyUrl { get; set; }

    public string? ForecastGridDataUrl { get; set; }

    /// <summary>UTC. Null until the gridpoint has been resolved once.</summary>
    public DateTimeOffset? GridpointResolvedAt { get; set; }

    /// <summary>UTC. Set by the application's clock, not a database default.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    public AlertRule? AlertRule { get; set; }
}
