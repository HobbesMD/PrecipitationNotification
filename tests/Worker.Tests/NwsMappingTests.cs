using System.Text.Json;
using Worker.Nws;

namespace Worker.Tests;

public class NwsMappingTests
{
    private const string Dir = "nws/2026-10-29T1900Z_pre-dst-fallback/";

    private static readonly NwsPoint Point = new("America/New_York", "https://example.test/hourly", null);

    private static NwsForecast MapFixture()
    {
        var props = FixtureFiles.Load<PointResponse>(Dir + "points.json").Properties!;
        var point = new NwsPoint(props.TimeZone!, props.ForecastHourly!, props.ForecastGridData);
        var hourly = FixtureFiles.Load<HourlyForecastResponse>(Dir + "forecast-hourly.json");
        var grid = FixtureFiles.Load<GridpointResponse>(Dir + "gridpoint.json");
        return NwsMapper.ToForecast(point, hourly.Properties!.Periods!, grid.Properties);
    }

    private static ForecastPeriod Period(string pop) =>
        JsonSerializer.Deserialize<ForecastPeriod>($$"""
            {"number":1,"startTime":"2026-10-30T20:00:00-04:00","endTime":"2026-10-30T21:00:00-04:00",
             "isDaytime":false,"temperature":50,"temperatureUnit":null,"shortForecast":null,
             "probabilityOfPrecipitation":{{pop}}}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Maps_all_hours_with_first_and_last_start()
    {
        var forecast = MapFixture();

        Assert.Equal(156, forecast.Hours.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-10-29T14:00:00-04:00"), forecast.Hours[0].Start);
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T00:00:00-05:00"), forecast.Hours[^1].Start);
    }

    [Fact]
    public void Repeated_fallback_hour_maps_to_two_distinct_one_hour_instants()
    {
        var repeated = MapFixture().Hours
            .Where(h => h.Start.DateTime == new DateTime(2026, 11, 1, 1, 0, 0))
            .ToList();

        Assert.Equal(2, repeated.Count);
        Assert.Equal(TimeSpan.FromHours(-4), repeated[0].Start.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), repeated[1].Start.Offset);
        Assert.All(repeated, h => Assert.Equal(TimeSpan.FromHours(1), h.Duration));
        Assert.Equal(repeated[0].End, repeated[1].Start);
    }

    [Fact]
    public void Time_zone_comes_from_points()
    {
        Assert.Equal("America/New_York", MapFixture().TimeZoneId);
    }

    [Fact]
    public void Amount_series_counts_match_fixture()
    {
        var grid = FixtureFiles.Load<GridpointResponse>(Dir + "gridpoint.json").Properties!;
        var amounts = MapFixture().Amounts!;

        Assert.Equal(13, grid.QuantitativePrecipitation!.Values!.Count);
        Assert.Equal(13, grid.SnowfallAmount!.Values!.Count);
        Assert.Equal(13, grid.IceAccumulation!.Values!.Count);
        Assert.Equal(13, amounts.Liquid.Count);
        Assert.Equal(13, amounts.Snow.Count);
        Assert.Equal(13, amounts.Ice.Count);
    }

    [Fact]
    public void Null_probability_maps_to_null_and_absent_text_gets_defaults()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("""{"value":null}""")], null).Hours);

        Assert.Null(hour.PrecipProbability);
        Assert.Equal("", hour.ShortForecast);
        Assert.Equal("F", hour.TemperatureUnit);
    }

    [Fact]
    public void Missing_probability_object_maps_to_null()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("null")], null).Hours);
        Assert.Null(hour.PrecipProbability);
    }

    [Fact]
    public void Probability_is_rounded()
    {
        var hour = Assert.Single(NwsMapper.ToForecast(Point, [Period("""{"value":54.6}""")], null).Hours);
        Assert.Equal(55, hour.PrecipProbability);
    }

    private static readonly GridSeries Ok = new("wmoUnit:mm", []);

    private static NwsForecast MapGrid(GridpointProperties? grid) => NwsMapper.ToForecast(Point, [], grid);

    private static void AssertUnavailable(NwsForecast forecast, string reasonContains)
    {
        Assert.Null(forecast.Amounts);
        Assert.Contains(reasonContains, forecast.AmountsUnavailableReason);
    }

    [Fact]
    public void Available_amounts_have_no_reason()
    {
        var forecast = MapFixture();
        Assert.NotNull(forecast.Amounts);
        Assert.Null(forecast.AmountsUnavailableReason);
    }

    [Fact]
    public void Null_grid_properties_make_amounts_unavailable() =>
        AssertUnavailable(MapGrid(null), "no properties");

    [Fact]
    public void Absent_snowfall_series_makes_amounts_unavailable() =>
        AssertUnavailable(MapGrid(new GridpointProperties(Ok, null, Ok)), "snowfallAmount");

    [Theory]
    [InlineData("wmoUnit:m")]
    [InlineData("wmoUnit:degC")]
    [InlineData(null)]
    public void Non_millimetre_unit_makes_amounts_unavailable(string? uom) =>
        AssertUnavailable(MapGrid(new GridpointProperties(Ok, new GridSeries(uom, []), Ok)), "snowfallAmount");

    [Fact]
    public void Malformed_valid_time_makes_amounts_unavailable()
    {
        var bad = new GridSeries("wmoUnit:mm", [new GridValue("garbage", 1)]);
        AssertUnavailable(MapGrid(new GridpointProperties(Ok, Ok, bad)), "iceAccumulation");
    }

    [Fact]
    public void Offsetless_valid_time_makes_amounts_unavailable()
    {
        var bad = new GridSeries("wmoUnit:mm", [new GridValue("2026-10-29T12:00:00/PT1H", 1)]);
        AssertUnavailable(MapGrid(new GridpointProperties(bad, Ok, Ok)), "quantitativePrecipitation");
    }

    [Fact]
    public void Present_but_empty_series_is_genuine_no_data()
    {
        var forecast = MapGrid(new GridpointProperties(Ok, Ok, Ok));

        Assert.Null(forecast.AmountsUnavailableReason);
        Assert.Empty(forecast.Amounts!.Liquid);
        Assert.Empty(forecast.Amounts.Snow);
        Assert.Empty(forecast.Amounts.Ice);
    }

    [Fact]
    public void Caller_supplied_reason_wins_and_amounts_are_null()
    {
        var forecast = NwsMapper.ToForecast(Point, [], new GridpointProperties(Ok, Ok, Ok), "gridpoint fetch failed: 500");
        AssertUnavailable(forecast, "500");
    }

    [Fact]
    public void Hourly_start_time_without_offset_fails_deserialization()
    {
        const string json = """
            {"number":1,"startTime":"2026-10-30T20:00:00","endTime":"2026-10-30T21:00:00-04:00",
             "isDaytime":false,"temperature":50}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ForecastPeriod>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
