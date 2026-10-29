using System.Text.Json;
using System.Text.Json.Serialization;
using Worker.Forecast;

namespace Worker.Nws;

// Just the slice of the api.weather.gov responses Block 1 needs. The real
// payloads are much larger; unmapped fields are ignored.

internal sealed record PointResponse(
    [property: JsonPropertyName("properties")] PointProperties? Properties);

internal sealed record PointProperties(
    [property: JsonPropertyName("forecastHourly")] string? ForecastHourly,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("forecastGridData")] string? ForecastGridData);

internal sealed record HourlyForecastResponse(
    [property: JsonPropertyName("properties")] HourlyForecastProperties? Properties);

internal sealed record HourlyForecastProperties(
    [property: JsonPropertyName("periods")] List<ForecastPeriod>? Periods);

internal sealed record ForecastPeriod(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("startTime"), JsonConverter(typeof(StrictOffsetConverter))] DateTimeOffset StartTime,
    [property: JsonPropertyName("endTime"), JsonConverter(typeof(StrictOffsetConverter))] DateTimeOffset EndTime,
    [property: JsonPropertyName("isDaytime")] bool IsDaytime,
    [property: JsonPropertyName("temperature")] int Temperature,
    [property: JsonPropertyName("temperatureUnit")] string? TemperatureUnit,
    [property: JsonPropertyName("probabilityOfPrecipitation")] QuantitativeValue? ProbabilityOfPrecipitation,
    [property: JsonPropertyName("shortForecast")] string? ShortForecast);

internal sealed record QuantitativeValue(
    [property: JsonPropertyName("value")] double? Value);

// Raw gridpoint data: the accumulation series that the hourly forecast lacks.

internal sealed record GridpointResponse(
    [property: JsonPropertyName("properties")] GridpointProperties? Properties);

internal sealed record GridpointProperties(
    [property: JsonPropertyName("quantitativePrecipitation")] GridSeries? QuantitativePrecipitation,
    [property: JsonPropertyName("snowfallAmount")] GridSeries? SnowfallAmount,
    [property: JsonPropertyName("iceAccumulation")] GridSeries? IceAccumulation);

internal sealed record GridSeries(
    [property: JsonPropertyName("uom")] string? Uom,
    [property: JsonPropertyName("values")] List<GridValue>? Values);

/// <summary><c>Value</c> is null when NWS has no number for the interval.</summary>
internal sealed record GridValue(
    [property: JsonPropertyName("validTime")] string ValidTime,
    [property: JsonPropertyName("value")] double? Value);

/// <summary>
/// Routes hourly timestamps through <see cref="StrictTimestamps"/> so an offset-less value fails
/// deserialization like any other bad payload, instead of being read in the host's zone.
/// </summary>
internal sealed class StrictOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return StrictTimestamps.Parse(reader.GetString() ?? "");
        }
        catch (FormatException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
