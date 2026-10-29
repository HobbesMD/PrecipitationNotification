namespace Worker.Forecast;

/// <summary>
/// One overnight alert window, pinned to absolute instants. <see cref="NightDate"/>
/// is the local date the window <em>starts</em> on — the forecast-night key that
/// idempotency will hang off in Block 4.
/// </summary>
internal sealed record NightWindow(DateOnly NightDate, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public TimeSpan Length => EndUtc - StartUtc;

    /// <summary>
    /// The first window (start on local date D, end on D+1) whose end is still in
    /// the future relative to <paramref name="now"/>.
    /// </summary>
    public static NightWindow Resolve(DateTimeOffset now, TimeZoneInfo zone, TimeOnly start, TimeOnly end) =>
        throw new NotImplementedException();
}
