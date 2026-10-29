using Worker.Forecast;

namespace Worker.Nws;

/// <summary>
/// An NWS forecast mapped onto provider-neutral types, plus the location's IANA zone.
/// <see cref="Amounts"/> is null when the gridpoint data couldn't be fetched or
/// trusted; <see cref="AmountsUnavailableReason"/> then says why. The hourly forecast
/// alone is enough to decide whether to alert.
/// </summary>
internal sealed record NwsForecast(
    string TimeZoneId,
    IReadOnlyList<HourlyForecast> Hours,
    PrecipAmounts? Amounts,
    string? AmountsUnavailableReason = null);
