using System.Xml;

namespace Worker.Forecast;

/// <summary>An amount (mm) forecast to fall over <c>[Start, Start + Duration)</c>.</summary>
internal sealed record IntervalAmount(DateTimeOffset Start, TimeSpan Duration, double Mm)
{
    public DateTimeOffset End => Start + Duration;
}

/// <summary>
/// Forecast accumulation series. <see cref="Liquid"/> is liquid-equivalent of
/// <em>all</em> precipitation (NWS <c>quantitativePrecipitation</c>), snow included —
/// it is not "rain".
/// </summary>
internal sealed record PrecipAmounts(
    IReadOnlyList<IntervalAmount> Liquid,
    IReadOnlyList<IntervalAmount> Snow,
    IReadOnlyList<IntervalAmount> Ice)
{
    public static PrecipAmounts Empty { get; } = new([], [], []);

    /// <summary>
    /// Millimetres of <paramref name="series"/> falling inside <c>[fromUtc, toUtc)</c>. Each interval
    /// contributes <c>Mm × overlap / Duration</c>: the amount is assumed to fall at an even rate
    /// across its interval. (Weighting by hourly probability of precipitation was rejected — PoP is
    /// a chance, not an intensity, so it would invent a distribution the data doesn't contain.)
    /// </summary>
    public static double Apportion(IReadOnlyList<IntervalAmount> series, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc)
        {
            return 0;
        }

        var total = 0.0;
        foreach (var interval in series)
        {
            var start = interval.Start > fromUtc ? interval.Start : fromUtc;
            var end = interval.End < toUtc ? interval.End : toUtc;
            var overlap = end - start;
            if (overlap <= TimeSpan.Zero)
            {
                continue;
            }

            total += interval.Mm * ((double)overlap.Ticks / interval.Duration.Ticks);
        }

        return total;
    }

    /// <summary>Parses an NWS <c>validTime</c> such as <c>2026-10-29T12:00:00+00:00/PT6H</c>. A null amount means 0. The start must carry a UTC offset.</summary>
    public static IntervalAmount ParseValidTime(string validTime, double? mm)
    {
        var parts = validTime.Split('/');
        if (parts.Length != 2)
        {
            throw new FormatException($"validTime '{validTime}' must be '<start>/<duration>'.");
        }

        DateTimeOffset start;
        try
        {
            start = StrictTimestamps.Parse(parts[0]);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"validTime '{validTime}' has an unusable start '{parts[0]}'.", ex);
        }

        TimeSpan duration;
        try
        {
            duration = XmlConvert.ToTimeSpan(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"validTime '{validTime}' has an invalid ISO-8601 duration '{parts[1]}'.", ex);
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new FormatException($"validTime '{validTime}' has a non-positive duration '{parts[1]}'.");
        }

        return new IntervalAmount(start, duration, mm ?? 0);
    }
}
