using System.Text.Json.Serialization;

namespace Worker.OpenMeteo;

// Wire shapes for Open-Meteo's /v1/forecast with timeformat=unixtime. Everything is nullable:
// the mapper decides what a missing field means, not the deserializer (the D7 rule for NWS).

/// <summary>The top-level <c>/v1/forecast</c> response.</summary>
/// <param name="Timezone">The IANA zone resolved by <c>timezone=auto</c>.</param>
/// <param name="UtcOffsetSeconds">
/// The zone's offset at request time only. It must never be used to place hours, because it is
/// wrong for every hour on the far side of a DST change.
/// </param>
/// <param name="HourlyUnits">Unit per hourly series, e.g. <c>precipitation: "mm"</c> and <c>snowfall: "cm"</c>.</param>
/// <param name="Hourly">The hourly series.</param>
internal sealed record OpenMeteoResponse(
    [property: JsonPropertyName("timezone")] string? Timezone,
    [property: JsonPropertyName("utc_offset_seconds")] int UtcOffsetSeconds,
    [property: JsonPropertyName("hourly_units")] Dictionary<string, string>? HourlyUnits,
    [property: JsonPropertyName("hourly")] OpenMeteoHourly? Hourly);

/// <summary>
/// Parallel arrays with one entry per hour. <see cref="Time"/> is unix seconds (UTC), so every
/// instant is unambiguous across DST with no local-time parsing. Whether a value covers the
/// hour starting or ending at its timestamp differs by variable; interpreting that is the
/// mapper's job.
/// </summary>
internal sealed record OpenMeteoHourly(
    [property: JsonPropertyName("time")] long[]? Time,
    [property: JsonPropertyName("precipitation_probability")] int?[]? PrecipitationProbability,
    [property: JsonPropertyName("precipitation")] double?[]? Precipitation,
    [property: JsonPropertyName("snowfall")] double?[]? Snowfall,
    [property: JsonPropertyName("weather_code")] int?[]? WeatherCode,
    [property: JsonPropertyName("temperature_2m")] double?[]? Temperature2m = null);
