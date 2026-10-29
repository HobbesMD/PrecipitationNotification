using Worker.Forecast;

namespace Worker.Nws;

/// <summary>Pure translation from NWS DTOs to provider-neutral forecast types.</summary>
internal static class NwsMapper
{
    private const string Millimetres = "wmoUnit:mm";

    public static NwsForecast ToForecast(
        PointProperties point,
        IReadOnlyList<ForecastPeriod> periods,
        GridpointProperties? grid)
    {
        var zone = point.TimeZone
            ?? throw new InvalidOperationException("NWS /points response had no timeZone.");

        var hours = periods.Select(p => new HourlyForecast(
            p.StartTime,
            p.EndTime - p.StartTime,
            p.ProbabilityOfPrecipitation?.Value is { } v ? (int)Math.Round(v) : null,
            p.ShortForecast ?? "",
            p.Temperature,
            p.TemperatureUnit ?? "F")).ToList();

        var amounts = new PrecipAmounts(
            MapSeries(grid?.QuantitativePrecipitation, "quantitativePrecipitation"),
            MapSeries(grid?.SnowfallAmount, "snowfallAmount"),
            MapSeries(grid?.IceAccumulation, "iceAccumulation"));

        return new NwsForecast(zone, hours, amounts);
    }

    // Refuse to guess at units: PrecipAmounts is millimetres end to end.
    private static IReadOnlyList<IntervalAmount> MapSeries(GridSeries? series, string name)
    {
        if (series is null)
            return [];

        if (series.Uom != Millimetres)
            throw new InvalidOperationException(
                $"NWS gridpoint {name} uses unit '{series.Uom}', expected '{Millimetres}'.");

        return (series.Values ?? [])
            .Select(v => PrecipAmounts.ParseValidTime(v.ValidTime, v.Value))
            .ToList();
    }
}
