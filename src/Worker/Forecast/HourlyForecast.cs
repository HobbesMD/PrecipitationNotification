namespace Worker.Forecast;

/// <summary>
/// One forecast period, provider-neutral. NWS hourly periods map onto this today;
/// Open-Meteo will in Block 5. <see cref="PrecipProbability"/> is a percentage,
/// null when the provider didn't supply one.
/// </summary>
internal sealed record HourlyForecast(
    DateTimeOffset Start,
    TimeSpan Duration,
    int? PrecipProbability,
    string ShortForecast,
    int Temperature,
    string TemperatureUnit)
{
    public DateTimeOffset End => Start + Duration;
}
