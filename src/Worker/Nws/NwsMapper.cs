using Worker.Forecast;

namespace Worker.Nws;

/// <summary>Pure translation from NWS DTOs to provider-neutral forecast types.</summary>
internal static class NwsMapper
{
    private const string Millimetres = "wmoUnit:mm";

    /// <summary>
    /// Amounts are optional: if <paramref name="amountsUnavailableReason"/> is given, or the grid
    /// data is missing, mis-united or malformed, the result carries null amounts and a reason
    /// rather than throwing or guessing zero. The zone comes from <paramref name="point"/>;
    /// a point without one can't exist, because <c>NwsClient.ResolvePointAsync</c> throws first.
    /// </summary>
    public static NwsForecast ToForecast(
        NwsPoint point,
        IReadOnlyList<ForecastPeriod> periods,
        GridpointProperties? grid,
        string? amountsUnavailableReason = null)
    {
        var hours = periods.Select(p => new HourlyForecast(
            p.StartTime,
            p.EndTime - p.StartTime,
            p.ProbabilityOfPrecipitation?.Value is { } v ? (int)Math.Round(v) : null,
            p.ShortForecast ?? "",
            p.Temperature,
            p.TemperatureUnit ?? "F")).ToList();

        if (amountsUnavailableReason is not null)
            return new NwsForecast(point.TimeZoneId, hours, null, amountsUnavailableReason);

        var amounts = TryMapAmounts(grid, out var reason);
        return new NwsForecast(point.TimeZoneId, hours, amounts, reason);
    }

    private static PrecipAmounts? TryMapAmounts(GridpointProperties? grid, out string? reason)
    {
        if (grid is null)
        {
            reason = "gridpoint response had no properties";
            return null;
        }

        var liquid = TryMapSeries(grid.QuantitativePrecipitation, "quantitativePrecipitation", out reason);
        if (liquid is null) return null;
        var snow = TryMapSeries(grid.SnowfallAmount, "snowfallAmount", out reason);
        if (snow is null) return null;
        var ice = TryMapSeries(grid.IceAccumulation, "iceAccumulation", out reason);
        if (ice is null) return null;

        return new PrecipAmounts(liquid, snow, ice);
    }

    // Refuse to guess at units: PrecipAmounts is millimetres end to end. An absent series is
    // unavailable; a present series with no values is genuine "no data" and maps to empty.
    private static List<IntervalAmount>? TryMapSeries(GridSeries? series, string name, out string? reason)
    {
        reason = null;
        if (series is null)
        {
            reason = $"gridpoint {name} series was absent";
            return null;
        }

        if (series.Uom != Millimetres)
        {
            reason = $"gridpoint {name} uses unit '{series.Uom}', expected '{Millimetres}'";
            return null;
        }

        try
        {
            return (series.Values ?? [])
                .Select(v => PrecipAmounts.ParseValidTime(v.ValidTime, v.Value))
                .ToList();
        }
        catch (FormatException ex)
        {
            reason = $"gridpoint {name} had a malformed validTime: {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            // e.g. a null validTime. Amounts are optional (D11), so any bad value degrades.
            reason = $"gridpoint {name} had an unusable value: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }
}
