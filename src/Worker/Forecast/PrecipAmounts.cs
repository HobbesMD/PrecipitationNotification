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

    /// <summary>Millimetres of <paramref name="series"/> falling inside <c>[fromUtc, toUtc)</c>.</summary>
    public static double Apportion(IReadOnlyList<IntervalAmount> series, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        throw new NotImplementedException();

    /// <summary>Parses an NWS <c>validTime</c> such as <c>2026-10-29T12:00:00+00:00/PT6H</c>.</summary>
    public static IntervalAmount ParseValidTime(string validTime, double? mm) =>
        throw new NotImplementedException();
}
