using Worker.Forecast;

namespace Worker.Nws;

/// <summary>An NWS forecast mapped onto provider-neutral types, plus the location's IANA zone.</summary>
internal sealed record NwsForecast(string TimeZoneId, IReadOnlyList<HourlyForecast> Hours, PrecipAmounts Amounts);
