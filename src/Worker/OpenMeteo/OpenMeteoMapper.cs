using Worker.Forecast;

namespace Worker.OpenMeteo;

/// <summary>Pure translation from Open-Meteo DTOs to provider-neutral forecast types.</summary>
internal static class OpenMeteoMapper
{
    /// <summary>
    /// Maps a <c>/v1/forecast</c> response to a <see cref="ProviderForecast"/> whose provider is
    /// <see cref="Providers.OpenMeteo"/>.
    /// </summary>
    public static ProviderForecast ToForecast(OpenMeteoResponse response) =>
        throw new NotImplementedException();
}
