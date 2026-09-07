using System.Text.Json.Serialization;

namespace Worker.Nws;

// Just the slice of the api.weather.gov responses Block 1 needs. The real
// payloads are much larger; unmapped fields are ignored.

internal sealed record PointResponse(
    [property: JsonPropertyName("properties")] PointProperties? Properties);

internal sealed record PointProperties(
    [property: JsonPropertyName("forecastHourly")] string? ForecastHourly,
    [property: JsonPropertyName("timeZone")] string? TimeZone);

internal sealed record HourlyForecastResponse(
    [property: JsonPropertyName("properties")] HourlyForecastProperties? Properties);

internal sealed record HourlyForecastProperties(
    [property: JsonPropertyName("periods")] List<ForecastPeriod>? Periods);

internal sealed record ForecastPeriod(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("startTime")] DateTimeOffset StartTime,
    [property: JsonPropertyName("endTime")] DateTimeOffset EndTime,
    [property: JsonPropertyName("isDaytime")] bool IsDaytime,
    [property: JsonPropertyName("temperature")] int Temperature,
    [property: JsonPropertyName("temperatureUnit")] string? TemperatureUnit,
    [property: JsonPropertyName("probabilityOfPrecipitation")] QuantitativeValue? ProbabilityOfPrecipitation,
    [property: JsonPropertyName("shortForecast")] string? ShortForecast);

internal sealed record QuantitativeValue(
    [property: JsonPropertyName("value")] double? Value);
