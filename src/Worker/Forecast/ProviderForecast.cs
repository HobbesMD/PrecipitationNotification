namespace Worker.Forecast;

/// <summary>
/// A forecast from any provider, already mapped onto provider-neutral types, plus the
/// location's IANA zone. The decision engine never sees a provider's JSON (D7), so NWS and
/// Open-Meteo both land here.
///
/// <see cref="Provider"/> names the source (one of <see cref="Providers"/>) so the message
/// and the alert record can say where the numbers came from once a fallback exists.
/// <see cref="Amounts"/> is null when the amount data couldn't be fetched or trusted;
/// <see cref="AmountsUnavailableReason"/> then says why. The hourly forecast alone is
/// enough to decide whether to alert (D11).
/// </summary>
internal sealed record ProviderForecast(
    string Provider,
    string TimeZoneId,
    IReadOnlyList<HourlyForecast> Hours,
    PrecipAmounts? Amounts,
    string? AmountsUnavailableReason = null);

/// <summary>
/// The provider names that appear in <see cref="ProviderForecast.Provider"/>, notification text
/// and <c>alert_history.provider</c>. Constants, not an enum: they are stored as text and shown
/// to people, and a constant can't be silently renumbered.
/// </summary>
internal static class Providers
{
    public const string Nws = "NWS";
    public const string OpenMeteo = "Open-Meteo";
}
