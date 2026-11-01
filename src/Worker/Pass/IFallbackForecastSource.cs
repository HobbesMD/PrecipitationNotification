using Worker.Forecast;
using Worker.OpenMeteo;

namespace Worker.Pass;

/// <summary>
/// The second forecast source <see cref="NightlyPass"/> turns to when NWS fails. It is keyed on
/// the location rather than an <see cref="Nws.NwsPoint"/>, because a fallback must not depend on
/// anything the primary provider resolved.
/// </summary>
internal interface IFallbackForecastSource
{
    Task<ProviderForecast> GetForecastAsync(Persistence.Location location, CancellationToken ct);
}

/// <summary><see cref="IFallbackForecastSource"/> over <see cref="OpenMeteoClient"/>.</summary>
internal sealed class OpenMeteoForecastSource(OpenMeteoClient client) : IFallbackForecastSource
{
    public Task<ProviderForecast> GetForecastAsync(Persistence.Location location, CancellationToken ct) =>
        client.GetForecastAsync(location.Latitude, location.Longitude, ct);
}
