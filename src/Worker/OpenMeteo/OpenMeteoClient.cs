using Worker.Forecast;

namespace Worker.OpenMeteo;

/// <summary>
/// Open-Meteo's <c>/v1/forecast</c> in a single request: there's no gridpoint step, because
/// it's keyed directly on coordinates. The coordinates are secret (D22), so neither they nor
/// the request URL may appear in an exception message or a log line.
/// </summary>
internal sealed class OpenMeteoClient(HttpClient http, OpenMeteoOptions options)
{
    /// <summary>Fetches and maps the hourly forecast for one location.</summary>
    public Task<ProviderForecast> GetForecastAsync(decimal lat, decimal lon, CancellationToken ct) =>
        throw new NotImplementedException();
}
