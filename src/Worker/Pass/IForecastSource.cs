using Worker.Nws;

namespace Worker.Pass;

/// <summary>
/// The forecast fetch as <see cref="NightlyPass"/> sees it. A seam so the pass can be tested
/// without HTTP, and so Open-Meteo (Block 5) can slot in behind the same call.
/// </summary>
internal interface IForecastSource
{
    /// <exception cref="NwsPointStaleException">The cached point no longer works; refresh it and retry.</exception>
    Task<NwsForecast> GetForecastAsync(NwsPoint point, CancellationToken ct);
}

/// <summary><see cref="IForecastSource"/> over <see cref="NwsClient"/>.</summary>
internal sealed class NwsForecastSource(NwsClient nws) : IForecastSource
{
    public Task<NwsForecast> GetForecastAsync(NwsPoint point, CancellationToken ct) =>
        nws.GetForecastAsync(point, ct);
}
