using System.Net.Http.Json;

namespace Worker.Nws;

/// <summary>
/// NWS lookup: <c>/points/{lat},{lon}</c> to discover the gridpoint's hourly
/// forecast and raw grid-data URLs (plus the location's time zone), then both
/// fetched concurrently.
///
/// The gridpoint URL is stable and is re-fetched every run for now; caching moves
/// to Block 4, where it belongs on the Location row (a container has no persistent
/// disk to cache on). No retry/backoff either — that's Block 5.
/// </summary>
internal sealed class NwsClient(HttpClient http)
{
    private const string PointsBase = "https://api.weather.gov/points/";

    /// <summary>Points → hourly forecast + raw gridpoint data, mapped to provider-neutral types.</summary>
    public async Task<NwsForecast> GetForecastAsync(string latLon, CancellationToken ct)
    {
        var point = await http.GetFromJsonAsync<PointResponse>(PointsBase + latLon, ct)
            ?? throw new InvalidOperationException("NWS /points returned an empty body.");
        var props = point.Properties
            ?? throw new InvalidOperationException("NWS /points response had no properties.");

        var hourlyUrl = props.ForecastHourly
            ?? throw new InvalidOperationException("NWS /points response had no forecastHourly URL.");
        var gridUrl = props.ForecastGridData
            ?? throw new InvalidOperationException("NWS /points response had no forecastGridData URL.");

        // Independent requests; no reason to serialize them.
        var hourlyTask = http.GetFromJsonAsync<HourlyForecastResponse>(hourlyUrl, ct);
        var gridTask = http.GetFromJsonAsync<GridpointResponse>(gridUrl, ct);
        await Task.WhenAll(hourlyTask, gridTask);

        var hourly = await hourlyTask
            ?? throw new InvalidOperationException("NWS hourly forecast returned an empty body.");
        var grid = await gridTask
            ?? throw new InvalidOperationException("NWS gridpoint data returned an empty body.");

        return NwsMapper.ToForecast(props, hourly.Properties?.Periods ?? [], grid.Properties);
    }
}
