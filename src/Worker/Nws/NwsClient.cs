using System.Net.Http.Json;
using System.Text.Json;

namespace Worker.Nws;

/// <summary>
/// NWS lookup: <c>/points/{lat},{lon}</c> to discover the gridpoint's hourly
/// forecast and raw grid-data URLs (plus the location's time zone), then both
/// fetched concurrently.
///
/// The hourly forecast alone decides whether to alert, so a gridpoint failure degrades
/// to "no amounts" instead of failing the run; a /points or hourly failure still throws.
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

        // Independent requests; no reason to serialize them.
        var hourlyTask = http.GetFromJsonAsync<HourlyForecastResponse>(hourlyUrl, ct);
        var gridTask = props.ForecastGridData is { } gridUrl
            ? TryGetGridpointAsync(gridUrl, ct)
            : Task.FromResult<(GridpointProperties?, string?)>((null, "points response had no forecastGridData URL"));
        await Task.WhenAll(hourlyTask, gridTask);

        var hourly = await hourlyTask
            ?? throw new InvalidOperationException("NWS hourly forecast returned an empty body.");
        var (grid, failure) = await gridTask;

        return NwsMapper.ToForecast(props, hourly.Properties?.Periods ?? [], grid, failure);
    }

    /// <summary>
    /// Never throws for a bad gridpoint: failures come back as a reason string. A timeout surfaces
    /// as <see cref="TaskCanceledException"/> without <paramref name="ct"/> being cancelled; real
    /// cancellation is not caught and propagates.
    /// </summary>
    private async Task<(GridpointProperties? Grid, string? Failure)> TryGetGridpointAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return (null, $"gridpoint fetch failed: {(int)response.StatusCode} {response.ReasonPhrase}");

            var body = await response.Content.ReadFromJsonAsync<GridpointResponse>(ct);
            return body is null
                ? (null, "gridpoint fetch failed: empty body")
                : (body.Properties, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"gridpoint fetch failed: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "gridpoint fetch failed: timeout");
        }
        catch (JsonException ex)
        {
            return (null, $"gridpoint fetch failed: invalid JSON ({ex.Message})");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Amounts are optional (D11): no gridpoint failure mode may cost us the alert.
            return (null, $"gridpoint fetch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
