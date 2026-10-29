using System.Net.Http.Json;

namespace Worker.Nws;

/// <summary>
/// Two-step NWS lookup: <c>/points/{lat},{lon}</c> to discover the gridpoint's
/// hourly forecast URL, then a GET on that URL for the periods.
///
/// Block 1 debt: the gridpoint URL is stable and should be cached, but here we
/// fetch it every run. No retry/backoff either — that's Block 5.
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

    // Block 1 path; removed when Program.cs is rewired
    public async Task<IReadOnlyList<ForecastPeriod>> GetHourlyForecastAsync(
        string latLon, CancellationToken ct)
    {
        var point = await http.GetFromJsonAsync<PointResponse>(PointsBase + latLon, ct)
            ?? throw new InvalidOperationException("NWS /points returned an empty body.");

        var hourlyUrl = point.Properties?.ForecastHourly
            ?? throw new InvalidOperationException("NWS /points response had no forecastHourly URL.");

        var forecast = await http.GetFromJsonAsync<HourlyForecastResponse>(hourlyUrl, ct)
            ?? throw new InvalidOperationException("NWS hourly forecast returned an empty body.");

        return forecast.Properties?.Periods ?? [];
    }
}
