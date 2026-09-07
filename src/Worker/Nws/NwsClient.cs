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
