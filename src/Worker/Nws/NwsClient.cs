using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Worker.Forecast;

namespace Worker.Nws;

/// <summary>
/// NWS lookup in two steps. <see cref="ResolvePointAsync"/> turns coordinates into the
/// stable gridpoint mapping (<c>/points/{lat},{lon}</c>: hourly and raw grid-data URLs plus
/// the location's time zone), which <see cref="IGridpointCache"/> stores on the location row.
/// <see cref="GetForecastAsync"/> then fetches the hourly forecast and gridpoint data
/// concurrently from that point.
///
/// The hourly forecast alone decides whether to alert, so a gridpoint failure degrades
/// to "no amounts" instead of failing the run; a /points or hourly failure still throws.
/// A 404 from the hourly URL means the cached mapping is stale and throws
/// <see cref="NwsPointStaleException"/> so the caller can re-resolve once.
///
/// <para>
/// Retries, timeouts and the breaker live in the clients' pipelines (D33), not here.
/// <paramref name="gridHttp"/> is a second client with its <i>own</i> pipeline and so its own
/// breaker: gridpoint failures must never open the breaker that guards <c>/points</c> and the
/// hourly forecast. With one shared breaker, a run of gridpoint 5xx while hourly is healthy would
/// trip it, and the next location's healthy hourly call would fail fast and fall back to
/// Open-Meteo, breaking D36's rule that a gridpoint-only failure is not a fallback trigger.
/// When null (tests that don't care), gridpoint requests share <paramref name="http"/>.
/// </para>
///
/// <para>
/// Every body is read with <see cref="HttpClient.GetAsync(string, CancellationToken)"/>, whose
/// default <see cref="HttpCompletionOption.ResponseContentRead"/> buffers the whole body inside
/// <c>SendAsync</c>, under <see cref="HttpClient.Timeout"/>. The JSON is then parsed from memory.
/// <c>GetFromJsonAsync</c> and <c>ResponseHeadersRead</c> are avoided: they return at the headers
/// and stream the body afterwards, outside the clock that the resilience extensions set.
/// </para>
/// </summary>
internal sealed class NwsClient(HttpClient http, NwsOptions options, HttpClient? gridHttp = null)
{
    private readonly string _pointsBase = options.BaseUrl + "points/";
    private readonly HttpClient _gridHttp = gridHttp ?? http;

    /// <summary>
    /// The <c>/points</c> lookup alone, for <see cref="IGridpointCache"/> to store.
    /// <paramref name="latLon"/> is <c>lat,lon</c> with at most 4 decimal places (see
    /// <see cref="NwsCoordinates"/>); it is secret (D22), so neither it nor the URL may
    /// appear in an exception message.
    /// </summary>
    public async Task<NwsPoint> ResolvePointAsync(string latLon, CancellationToken ct)
    {
        // Buffered inside SendAsync, so a body that stalls is bounded by HttpClient.Timeout.
        // EnsureSuccessStatusCode's message names the status only, never the URL (D22).
        using var response = await http.GetAsync(_pointsBase + latLon, ct);
        response.EnsureSuccessStatusCode();
        var point = await response.Content.ReadFromJsonAsync<PointResponse>(ct)
            ?? throw new InvalidOperationException("NWS /points returned an empty body.");
        var props = point.Properties
            ?? throw new InvalidOperationException("NWS /points response had no properties.");

        // Nothing downstream can place the overnight window without the zone.
        var zone = props.TimeZone
            ?? throw new InvalidOperationException("NWS /points response had no timeZone.");
        var hourlyUrl = props.ForecastHourly
            ?? throw new InvalidOperationException("NWS /points response had no forecastHourly URL.");

        return new NwsPoint(zone, hourlyUrl, props.ForecastGridData);
    }

    /// <summary>
    /// Hourly forecast + gridpoint amounts from an already-resolved <paramref name="point"/>,
    /// skipping <c>/points</c>. Throws <see cref="NwsPointStaleException"/> when the hourly URL
    /// answers 404, so the caller can refresh the point and retry; any other non-2xx throws
    /// <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<ProviderForecast> GetForecastAsync(NwsPoint point, CancellationToken ct)
    {
        // Independent requests; no reason to serialize them.
        var hourlyTask = GetHourlyAsync(point.ForecastHourlyUrl, ct);
        var gridTask = point.ForecastGridDataUrl is { } gridUrl
            ? TryGetGridpointAsync(gridUrl, ct)
            : Task.FromResult<(GridpointProperties?, string?)>((null, "points response had no forecastGridData URL"));
        await Task.WhenAll(hourlyTask, gridTask);

        var hourly = await hourlyTask
            ?? throw new InvalidOperationException("NWS hourly forecast returned an empty body.");
        var (grid, failure) = await gridTask;

        return NwsMapper.ToForecast(point, hourly.Properties?.Periods ?? [], grid, failure);
    }

    private async Task<HourlyForecastResponse?> GetHourlyAsync(string url, CancellationToken ct)
    {
        // GetAsync buffers the body under HttpClient.Timeout; the read below is from memory.
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new NwsPointStaleException("NWS hourly forecast returned 404; the cached gridpoint is stale.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HourlyForecastResponse>(ct);
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
            // The grid client: its own pipeline and breaker (see the class summary).
            using var response = await _gridHttp.GetAsync(url, ct);
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
