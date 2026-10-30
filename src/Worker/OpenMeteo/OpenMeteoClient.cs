using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Worker.Forecast;

namespace Worker.OpenMeteo;

/// <summary>
/// Open-Meteo's <c>/v1/forecast</c> in a single request: there's no gridpoint step, because
/// it's keyed directly on coordinates. The coordinates are secret (D22), so neither they nor
/// the request URL may appear in an exception message or a log line.
/// </summary>
internal sealed class OpenMeteoClient(HttpClient http, OpenMeteoOptions options)
{
    private const string Query =
        "&hourly=precipitation_probability,precipitation,snowfall,weather_code,temperature_2m&temperature_unit=fahrenheit&timeformat=unixtime&timezone=auto&forecast_days=4";

    /// <summary>
    /// Fetches and maps the hourly forecast for one location. A non-2xx answer throws
    /// <see cref="HttpRequestException"/> (its message names the status, not the URL); an empty or
    /// unparseable body throws <see cref="InvalidOperationException"/> without echoing the body.
    /// </summary>
    public async Task<ProviderForecast> GetForecastAsync(decimal lat, decimal lon, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{options.BaseUrl}v1/forecast?latitude={Math.Round(lat, 4):0.####}&longitude={Math.Round(lon, 4):0.####}{Query}");

        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        OpenMeteoResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<OpenMeteoResponse>(ct);
        }
        catch (JsonException)
        {
            // No inner exception or body: JsonException messages can quote the payload.
            throw new InvalidOperationException("Open-Meteo /v1/forecast returned invalid JSON.");
        }

        return OpenMeteoMapper.ToForecast(
            body ?? throw new InvalidOperationException("Open-Meteo /v1/forecast returned an empty body."));
    }
}
