using System.Diagnostics;

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
    /// <remarks>
    /// Only D = yesterday and D = today are candidates. Today's window ends on
    /// tomorrow's local date, so it always ends after <paramref name="now"/>; a
    /// "tomorrow" fallback could never be the right answer, only a wrong
    /// idempotency key hiding a bug, so that case throws instead.
    /// </remarks>
    public static NightWindow Resolve(DateTimeOffset now, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        foreach (var night in (ReadOnlySpan<DateOnly>)[today.AddDays(-1), today])
        {
            var window = new NightWindow(
                night,
                ToInstant(night, start, zone, ambiguous: Occurrence.First),
                ToInstant(night.AddDays(1), end, zone, ambiguous: Occurrence.Second));

            if (window.EndUtc > now)
                return window;
        }

        throw new UnreachableException(
            $"No overnight window starting {today.AddDays(-1)} or {today} in {zone.Id} ends after {now:o}.");
    }

    private enum Occurrence { First, Second }

    /// <summary>
    /// Pins a local wall-clock time in <paramref name="zone"/> to a UTC instant.
    /// DST makes this a relation, not a function, so two rules decide it:
    /// a time skipped by spring-forward moves forward by the size of the gap
    /// (02:30 in a 02:00→03:00 gap becomes 03:30 at the new offset); a time
    /// repeated by fall-back takes the requested occurrence, so the caller can keep
    /// the window as wide as possible.
    /// </summary>
    private static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone, Occurrence ambiguous)
    {
        // Unspecified kind: TimeZoneInfo reads it as wall-clock time in `zone`
        // rather than converting from the machine's local zone or from UTC.
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (zone.IsAmbiguousTime(local))
        {
            // The larger offset is the pre-transition one, i.e. the earlier instant.
            // Don't rely on the order GetAmbiguousTimeOffsets returns them in.
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var offset = ambiguous == Occurrence.First ? offsets.Max() : offsets.Min();
            return new DateTimeOffset(local, offset).ToUniversalTime();
        }

        if (zone.IsInvalidTime(local))
        {
            // GetUtcOffset on an invalid time returns *an* offset, not documented
            // as the pre- or post-gap one. Probe the instant it implies: that instant
            // lands on the other side of the transition, so the two offsets are the
            // ones bracketing the gap. Clocks only skip forward when the offset
            // grows, so the smaller is "before".
            var guess = zone.GetUtcOffset(local);
            var other = zone.GetUtcOffset(new DateTimeOffset(local - guess, TimeSpan.Zero));
            var before = guess < other ? guess : other;
            var after = guess < other ? other : guess;

            var gap = after - before;
            return new DateTimeOffset(local + gap, after).ToUniversalTime();
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
